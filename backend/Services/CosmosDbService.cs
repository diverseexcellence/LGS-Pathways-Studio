using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

public interface ICosmosDbService
{
    // Admins
    Task<AdminDocument?> GetAdminByEmailAsync(string email);
    Task UpsertAdminAsync(AdminDocument admin);

    // Students
    Task<StudentDocument?> GetStudentAsync(string studentId);
    Task<(List<StudentDocument> Items, int Total)> ListStudentsAsync(int page, int pageSize, string? search, string? classGroup, bool activeOnly = true);
    Task UpsertStudentAsync(StudentDocument student);
    Task MoveStudentPartitionAsync(StudentDocument student, string oldClassGroup);
    Task<StudentDocument?> FindStudentByNameAndDobAsync(string fullName, string dob);
    Task<StudentDocument?> FindStudentByStnAsync(string stn);
    Task<StudentDocument?> FindStudentByLocalIdAsync(string localId);
    Task<int> DeleteStudentsWhereNameIsNumericAsync();
    Task<int> DeduplicateStudentsAsync();

    // Assessments
    Task<List<AssessmentDocument>> GetAssessmentsAsync(string studentId, string? subject = null);
    Task<List<AssessmentDocument>> GetAllAssessmentsAsync();
    Task CreateAssessmentAsync(AssessmentDocument assessment);
    Task DeleteAssessmentsByFileNameAsync(string fileName);
    Task<int> DeleteAllAssessmentsAsync();

    // AI Summaries
    Task<AiSummaryDocument?> GetLatestSummaryAsync(string studentId);
    Task CreateSummaryAsync(AiSummaryDocument summary);

    // Upload Logs
    Task<List<UploadLogDocument>> GetUploadLogsAsync();
    Task CreateUploadLogAsync(UploadLogDocument log);
    Task<UploadLogDocument?> GetUploadLogAsync(string id);
    Task DeleteUploadLogAsync(string id, string uploadedBy);
    Task<UploadLogDocument?> FindUploadLogByHashAsync(string contentHash);

    // Export Logs
    Task CreateExportLogAsync(ExportLogDocument log);

    // Audit Logs
    Task CreateAuditLogAsync(AuditLogDocument log);
    Task<(List<AuditLogDocument> Items, int Total)> GetAuditLogsAsync(int page, int pageSize, string? eventType);
    Task<(List<AuditLogDocument> Items, int Total)> GetAuditLogsByEntityIdAsync(string entityId, int page, int pageSize);

    // Collaboration Notes
    Task<List<CollaborationNoteDocument>> GetNotesAsync(string studentId);
    Task<CollaborationNoteDocument?> GetNoteAsync(string studentId, string noteId);
    Task CreateNoteAsync(CollaborationNoteDocument note);
    Task UpsertNoteAsync(CollaborationNoteDocument note);

    // Config (school averages + prompt)
    Task<SchoolAverageDocument?> GetSchoolAveragesAsync();
    Task UpsertSchoolAveragesAsync(SchoolAverageDocument doc);
    Task<PromptConfigDocument?> GetPromptConfigAsync();
    Task UpsertPromptConfigAsync(PromptConfigDocument doc);

    // Target Goal config
    Task<TargetGoalDocument> GetTargetGoalAsync();
    Task UpsertTargetGoalAsync(TargetGoalDocument doc);

    // Seed admins if container is empty
    Task SeedAdminsIfEmptyAsync();
}

public class CosmosDbService : ICosmosDbService
{
    private readonly CosmosClient _client;
    private readonly string _databaseId;

    private Container Admins => _client.GetContainer(_databaseId, "admins");
    private Container Students => _client.GetContainer(_databaseId, "students");
    private Container Assessments => _client.GetContainer(_databaseId, "assessments");
    private Container AiSummaries => _client.GetContainer(_databaseId, "ai-summaries");
    private Container UploadLogs => _client.GetContainer(_databaseId, "upload-logs");
    private Container ExportLogs => _client.GetContainer(_databaseId, "export-logs");
    private Container AuditLogs => _client.GetContainer(_databaseId, "audit-logs");
    private Container Config => _client.GetContainer(_databaseId, "config");
    private Container CollaborationNotes => _client.GetContainer(_databaseId, "collaboration-notes");

    public CosmosDbService(IConfiguration config)
    {
        var endpoint = config["Cosmos:Endpoint"]
            ?? throw new InvalidOperationException("Cosmos:Endpoint not configured");
        var key = config["Cosmos:Key"]
            ?? throw new InvalidOperationException("Cosmos:Key not configured");
        _databaseId = config["Cosmos:DatabaseId"] ?? "lgs-impact";

        _client = new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        });
    }

    // ─── Admins ──────────────────────────────────────────────────────────────

    public async Task<AdminDocument?> GetAdminByEmailAsync(string email)
    {
        var query = Admins.GetItemLinqQueryable<AdminDocument>()
            .Where(a => a.Email == email && a.IsActive)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            var item = page.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task UpsertAdminAsync(AdminDocument admin)
        => await Admins.UpsertItemAsync(admin, new PartitionKey(admin.Email));

    // ─── Students ────────────────────────────────────────────────────────────

    public async Task<StudentDocument?> GetStudentAsync(string studentId)
    {
        var query = Students.GetItemLinqQueryable<StudentDocument>()
            .Where(s => s.StudentId == studentId)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            var item = page.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task<(List<StudentDocument> Items, int Total)> ListStudentsAsync(
        int page, int pageSize, string? search, string? classGroup, bool activeOnly = true)
    {
        var queryable = Students.GetItemLinqQueryable<StudentDocument>().AsQueryable();

        if (activeOnly) queryable = queryable.Where(s => s.IsActive);
        if (!string.IsNullOrWhiteSpace(classGroup)) queryable = queryable.Where(s => s.ClassGroup == classGroup);

        // Fetch all matching (Cosmos LINQ doesn't support Skip/Take with Count in one query)
        var allItems = new List<StudentDocument>();
        var iterator = queryable.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            var pg = await iterator.ReadNextAsync();
            allItems.AddRange(pg);
        }

        // Apply search filter in memory (Cosmos free-text search requires Search API)
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            allItems = allItems.Where(x =>
                x.FullName.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                x.ClassGroup.Contains(s, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        allItems = allItems.OrderBy(s => s.FullName).ToList();
        var total = allItems.Count;
        var items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return (items, total);
    }

    public async Task UpsertStudentAsync(StudentDocument student)
        => await Students.UpsertItemAsync(student, new PartitionKey(student.ClassGroup));

    public async Task MoveStudentPartitionAsync(StudentDocument student, string oldClassGroup)
    {
        if (!string.Equals(oldClassGroup, student.ClassGroup, StringComparison.Ordinal))
        {
            try { await Students.DeleteItemAsync<StudentDocument>(student.Id, new PartitionKey(oldClassGroup)); }
            catch (CosmosException) { /* already gone */ }
        }
        await Students.UpsertItemAsync(student, new PartitionKey(student.ClassGroup));
    }

    public async Task<StudentDocument?> FindStudentByNameAndDobAsync(string fullName, string dob)
    {
        var query = Students.GetItemLinqQueryable<StudentDocument>()
            .Where(s => s.FullName == fullName && s.Dob == dob)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            var item = page.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task<StudentDocument?> FindStudentByStnAsync(string stn)
    {
        var query = Students.GetItemLinqQueryable<StudentDocument>()
            .Where(s => s.Stn == stn && s.IsActive)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            var item = page.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task<int> DeleteStudentsWhereNameIsNumericAsync()
    {
        var all = new List<StudentDocument>();
        var q = Students.GetItemLinqQueryable<StudentDocument>().ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            all.AddRange(pg);
        }

        var toDelete = all.Where(s => s.FullName.All(c => char.IsDigit(c) || c == ' ')).ToList();
        foreach (var s in toDelete)
            await Students.DeleteItemAsync<StudentDocument>(s.Id, new PartitionKey(s.ClassGroup));
        return toDelete.Count;
    }

    public async Task<int> DeduplicateStudentsAsync()
    {
        // Use raw SQL cross-partition query — LINQ cross-partition has known silent-empty issues
        var all = new List<StudentDocument>();
        var q = Students.GetItemQueryIterator<StudentDocument>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = -1 });
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            all.AddRange(pg);
        }
        // Dedupe phantom cross-partition copies: same studentId in multiple partitions
        // (caused by ClassGroup changing during enrichment without deleting the old record)
        var partitionGroups = all.GroupBy(s => s.StudentId).Where(g => g.Count() > 1);
        foreach (var pg in partitionGroups)
        {
            var copies = pg.OrderByDescending(s => s.Stn != null ? 1 : 0)
                           .ThenByDescending(s => s.ClassGroup != "Unassigned" ? 1 : 0)
                           .ThenByDescending(s => s.LastUpdated)
                           .ToList();
            var keep = copies.First();
            foreach (var stale in copies.Skip(1))
            {
                try { await Students.DeleteItemAsync<StudentDocument>(stale.Id, new PartitionKey(stale.ClassGroup)); }
                catch (CosmosException) { }
            }
        }
        // Reload after phantom dedup
        all = new List<StudentDocument>();
        var q2 = Students.GetItemQueryIterator<StudentDocument>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = -1 });
        while (q2.HasMoreResults)
        {
            var pg2 = await q2.ReadNextAsync();
            all.AddRange(pg2);
        }
        all = all.Where(s => s.IsActive).ToList();

        // Group by normalized full name (case-insensitive) for true name duplicates
        var groups = all.GroupBy(s => s.FullName.Trim().ToLowerInvariant())
                        .Where(g => g.Count() > 1);

        int merged = 0;
        foreach (var group in groups)
        {
            var dupes = group.ToList();
            // Keep the most enriched: prefer one with STN, then real ClassGroup, then most recent LastUpdated
            var keeper = dupes
                .OrderByDescending(s => s.Stn != null ? 1 : 0)
                .ThenByDescending(s => s.ClassGroup != "Unassigned" ? 1 : 0)
                .ThenByDescending(s => s.LastUpdated)
                .First();

            foreach (var dupe in dupes.Where(s => s.StudentId != keeper.StudentId))
            {
                // Reassign assessments from dupe → keeper (new id required — partition key changes)
                var assessments = await GetAssessmentsAsync(dupe.StudentId);
                foreach (var a in assessments)
                {
                    await Assessments.DeleteItemAsync<AssessmentDocument>(a.Id, new PartitionKey(a.StudentId));
                    a.Id = $"a-{Guid.NewGuid():N}";
                    a.StudentId = keeper.StudentId;
                    await Assessments.CreateItemAsync(a, new PartitionKey(a.StudentId));
                }

                // Soft-delete the duplicate student
                dupe.IsActive = false;
                dupe.LastUpdated = DateTime.UtcNow.ToString("o");
                await Students.UpsertItemAsync(dupe, new PartitionKey(dupe.ClassGroup));
                merged++;
            }
        }
        return merged;
    }

    public async Task<StudentDocument?> FindStudentByLocalIdAsync(string localId)
    {
        var query = Students.GetItemLinqQueryable<StudentDocument>()
            .Where(s => s.LocalId == localId && s.IsActive)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            var item = page.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    // ─── Assessments ─────────────────────────────────────────────────────────

    public async Task<List<AssessmentDocument>> GetAssessmentsAsync(string studentId, string? subject = null)
    {
        var q = Assessments.GetItemLinqQueryable<AssessmentDocument>()
            .Where(a => a.StudentId == studentId);

        if (!string.IsNullOrWhiteSpace(subject))
            q = q.Where(a => a.Subject == subject);

        var items = new List<AssessmentDocument>();
        var iter = q.ToFeedIterator();
        while (iter.HasMoreResults)
        {
            var pg = await iter.ReadNextAsync();
            items.AddRange(pg);
        }
        return items.OrderByDescending(a => a.Date).ToList();
    }

    public async Task<List<AssessmentDocument>> GetAllAssessmentsAsync()
    {
        var all = new List<AssessmentDocument>();
        var q = Assessments.GetItemQueryIterator<AssessmentDocument>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = -1 });
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            all.AddRange(pg);
        }
        return all;
    }

    public async Task CreateAssessmentAsync(AssessmentDocument assessment)
        => await Assessments.CreateItemAsync(assessment, new PartitionKey(assessment.StudentId));

    public async Task<int> DeleteAllAssessmentsAsync()
    {
        var all = new List<AssessmentDocument>();
        var q = Assessments.GetItemQueryIterator<AssessmentDocument>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = -1 });
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            all.AddRange(pg);
        }
        foreach (var a in all)
            await Assessments.DeleteItemAsync<AssessmentDocument>(a.Id, new PartitionKey(a.StudentId));
        return all.Count;
    }

    public async Task DeleteAssessmentsByFileNameAsync(string fileName)
    {
        var q = Assessments.GetItemLinqQueryable<AssessmentDocument>()
            .Where(a => a.FileName == fileName)
            .ToFeedIterator();

        var toDelete = new List<AssessmentDocument>();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            toDelete.AddRange(pg);
        }

        foreach (var item in toDelete)
            await Assessments.DeleteItemAsync<AssessmentDocument>(item.Id, new PartitionKey(item.StudentId));
    }

    // ─── AI Summaries ─────────────────────────────────────────────────────────

    public async Task<AiSummaryDocument?> GetLatestSummaryAsync(string studentId)
    {
        var items = new List<AiSummaryDocument>();
        var q = AiSummaries.GetItemLinqQueryable<AiSummaryDocument>()
            .Where(s => s.StudentId == studentId)
            .ToFeedIterator();

        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            items.AddRange(pg);
        }
        return items.OrderByDescending(s => s.GeneratedAt).FirstOrDefault();
    }

    public async Task CreateSummaryAsync(AiSummaryDocument summary)
        => await AiSummaries.CreateItemAsync(summary, new PartitionKey(summary.StudentId));

    // ─── Upload Logs ─────────────────────────────────────────────────────────

    public async Task<List<UploadLogDocument>> GetUploadLogsAsync()
    {
        var items = new List<UploadLogDocument>();
        var q = UploadLogs.GetItemLinqQueryable<UploadLogDocument>().ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            items.AddRange(pg);
        }
        return items.OrderByDescending(l => l.UploadedAt).ToList();
    }

    public async Task CreateUploadLogAsync(UploadLogDocument log)
        => await UploadLogs.CreateItemAsync(log, new PartitionKey(log.UploadedBy));

    public async Task<UploadLogDocument?> GetUploadLogAsync(string id)
    {
        var q = UploadLogs.GetItemLinqQueryable<UploadLogDocument>()
            .Where(l => l.Id == id)
            .ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            var item = pg.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task DeleteUploadLogAsync(string id, string uploadedBy)
        => await UploadLogs.DeleteItemAsync<UploadLogDocument>(id, new PartitionKey(uploadedBy));

    public async Task<UploadLogDocument?> FindUploadLogByHashAsync(string contentHash)
    {
        var q = UploadLogs.GetItemLinqQueryable<UploadLogDocument>()
            .Where(l => l.ContentHash == contentHash)
            .ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            var item = pg.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    // ─── Export Logs ─────────────────────────────────────────────────────────

    public async Task CreateExportLogAsync(ExportLogDocument log)
        => await ExportLogs.CreateItemAsync(log, new PartitionKey(log.ExportedBy));

    // ─── Audit Logs ──────────────────────────────────────────────────────────

    public async Task CreateAuditLogAsync(AuditLogDocument log)
        => await AuditLogs.CreateItemAsync(log, new PartitionKey(log.AdminEmail));

    public async Task<(List<AuditLogDocument> Items, int Total)> GetAuditLogsAsync(
        int page, int pageSize, string? eventType)
    {
        var queryable = AuditLogs.GetItemLinqQueryable<AuditLogDocument>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(eventType))
            queryable = queryable.Where(l => l.EventType == eventType);

        var all = new List<AuditLogDocument>();
        var iter = queryable.ToFeedIterator();
        while (iter.HasMoreResults)
        {
            var pg = await iter.ReadNextAsync();
            all.AddRange(pg);
        }

        all = all.OrderByDescending(l => l.Timestamp).ToList();
        var total = all.Count;
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    // ─── Collaboration Notes ──────────────────────────────────────────────────

    public async Task<List<CollaborationNoteDocument>> GetNotesAsync(string studentId)
    {
        var items = new List<CollaborationNoteDocument>();
        var q = CollaborationNotes.GetItemLinqQueryable<CollaborationNoteDocument>()
            .Where(n => n.StudentId == studentId && !n.IsDeleted)
            .ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            items.AddRange(pg);
        }
        return items.OrderByDescending(n => n.CreatedAt).ToList();
    }

    public async Task<CollaborationNoteDocument?> GetNoteAsync(string studentId, string noteId)
    {
        var q = CollaborationNotes.GetItemLinqQueryable<CollaborationNoteDocument>()
            .Where(n => n.Id == noteId && n.StudentId == studentId)
            .ToFeedIterator();
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            var item = pg.FirstOrDefault();
            if (item != null) return item;
        }
        return null;
    }

    public async Task CreateNoteAsync(CollaborationNoteDocument note)
        => await CollaborationNotes.CreateItemAsync(note, new PartitionKey(note.StudentId));

    public async Task UpsertNoteAsync(CollaborationNoteDocument note)
        => await CollaborationNotes.UpsertItemAsync(note, new PartitionKey(note.StudentId));

    public async Task<(List<AuditLogDocument> Items, int Total)> GetAuditLogsByEntityIdAsync(
        string entityId, int page, int pageSize)
    {
        // Cross-partition query required since audit logs are partitioned by adminEmail
        var sql = new QueryDefinition(
            "SELECT * FROM c WHERE c.entityId = @entityId ORDER BY c.timestamp DESC")
            .WithParameter("@entityId", entityId);

        var all = new List<AuditLogDocument>();
        var iter = AuditLogs.GetItemQueryIterator<AuditLogDocument>(
            sql, requestOptions: new QueryRequestOptions { MaxItemCount = -1 });
        while (iter.HasMoreResults)
        {
            var pg = await iter.ReadNextAsync();
            all.AddRange(pg);
        }

        var total = all.Count;
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (items, total);
    }

    // ─── Seed ────────────────────────────────────────────────────────────────

    public async Task SeedAdminsIfEmptyAsync()
    {
        var q = Admins.GetItemLinqQueryable<AdminDocument>().ToFeedIterator();
        var any = false;
        while (q.HasMoreResults)
        {
            var pg = await q.ReadNextAsync();
            if (pg.Any()) { any = true; break; }
        }
        if (any) return;

        var admins = new[]
        {
            new AdminDocument
            {
                Id   = "admin-1",
                AdminId = 1,
                Email = "velvet@lgs.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Ch@ng3Me!Velvet"),
                Name  = "Velvet",
                IsActive = true,
                CreatedAt = DateTime.UtcNow.ToString("o")
            },
            new AdminDocument
            {
                Id   = "admin-2",
                AdminId = 2,
                Email = "maurice@lgs.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Ch@ng3Me!Maurice"),
                Name  = "Maurice",
                IsActive = true,
                CreatedAt = DateTime.UtcNow.ToString("o")
            }
        };

        foreach (var admin in admins)
            await Admins.CreateItemAsync(admin, new PartitionKey(admin.Email));
    }

    // ─── Config ───────────────────────────────────────────────────────────────

    public async Task<SchoolAverageDocument?> GetSchoolAveragesAsync()
    {
        try
        {
            var res = await Config.ReadItemAsync<SchoolAverageDocument>("school-averages", new PartitionKey("school-averages"));
            return res.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertSchoolAveragesAsync(SchoolAverageDocument doc)
        => await Config.UpsertItemAsync(doc, new PartitionKey(doc.PartitionKey));

    public async Task<PromptConfigDocument?> GetPromptConfigAsync()
    {
        try
        {
            var res = await Config.ReadItemAsync<PromptConfigDocument>("ai-summary-prompt", new PartitionKey("prompts"));
            return res.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertPromptConfigAsync(PromptConfigDocument doc)
        => await Config.UpsertItemAsync(doc, new PartitionKey(doc.PartitionKey));

    public async Task<TargetGoalDocument> GetTargetGoalAsync()
    {
        try
        {
            var resp = await Config.ReadItemAsync<TargetGoalDocument>("target-goal", new PartitionKey("config"));
            return resp.Resource;
        }
        catch (CosmosException) { return new TargetGoalDocument(); }
    }

    public async Task UpsertTargetGoalAsync(TargetGoalDocument doc)
        => await Config.UpsertItemAsync(doc, new PartitionKey(doc.PartitionKey));

    // ─── Ensure containers exists ─────────────────────────────────────────────
    public static async Task EnsureDatabaseAndContainersAsync(CosmosClient client, string databaseId)
    {
        var db = await client.CreateDatabaseIfNotExistsAsync(databaseId);

        var containers = new[]
        {
            ("admins",       "/email"),
            ("students",     "/classGroup"),
            ("assessments",  "/studentId"),
            ("ai-summaries", "/studentId"),
            ("upload-logs",  "/uploadedBy"),
            ("export-logs",  "/exportedBy"),
            ("audit-logs",             "/adminEmail"),
            ("config",                 "/partitionKey"),
            ("collaboration-notes",    "/studentId"),
        };

        foreach (var (name, partitionKey) in containers)
            await db.Database.CreateContainerIfNotExistsAsync(name, partitionKey);
    }
}
