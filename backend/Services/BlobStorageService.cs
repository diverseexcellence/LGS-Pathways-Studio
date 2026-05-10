using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace LgsImpact.Api.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<string> GetSasUrlAsync(string blobName, int expiryMinutes = 60);
    Task<List<LandingZoneFile>> ListLandingZoneFilesAsync(CancellationToken ct = default);
}

public sealed class LandingZoneFile
{
    public string Name { get; set; } = string.Empty;
    public Stream Content { get; set; } = Stream.Null;
}

public class BlobStorageService(IConfiguration config) : IBlobStorageService
{
    private BlobContainerClient GetContainer()
    {
        var connStr = config["AzureBlob:ConnectionString"];
        var container = config["AzureBlob:ContainerName"] ?? "lgs-uploads";

        if (string.IsNullOrEmpty(connStr))
        {
            // Local dev: use Azurite or skip blob
            throw new InvalidOperationException("Azure Blob connection string not configured. Set AzureBlob:ConnectionString in appsettings.");
        }

        var client = new BlobContainerClient(connStr, container);
        client.CreateIfNotExists(PublicAccessType.None);
        return client;
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        var container = GetContainer();
        var blobName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{fileName}";
        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(stream, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        return blobName;
    }

    public async Task<string> GetSasUrlAsync(string blobName, int expiryMinutes = 60)
    {
        var container = GetContainer();
        var blob = container.GetBlobClient(blobName);
        var sasUri = blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddMinutes(expiryMinutes));
        return await Task.FromResult(sasUri.ToString());
    }

    public async Task<List<LandingZoneFile>> ListLandingZoneFilesAsync(CancellationToken ct = default)
    {
        var connStr = config["LandingZone:ConnectionString"] ?? config["AzureBlob:ConnectionString"];
        if (string.IsNullOrEmpty(connStr))
            throw new InvalidOperationException("Landing zone connection string not configured. Set LandingZone:ConnectionString in App Service environment variables.");

        var landingZone = new BlobContainerClient(connStr, "landing-zone");
        var results = new List<LandingZoneFile>();

        await foreach (var item in landingZone.GetBlobsAsync(cancellationToken: ct))
        {
            var ext = Path.GetExtension(item.Name).ToLowerInvariant();
            if (ext is not ".csv" and not ".xlsx") continue;

            var blob = landingZone.GetBlobClient(item.Name);
            var ms = new MemoryStream();
            await blob.DownloadToAsync(ms, ct);
            ms.Position = 0;
            results.Add(new LandingZoneFile { Name = item.Name, Content = ms });
        }

        return results;
    }
}
