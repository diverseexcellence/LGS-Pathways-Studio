using LgsImpact.Api.Models;
using LgsImpact.Api.Services;

namespace LgsImpact.Api.Tests.Services;

public class DashboardMetricsTests
{
    private static AssessmentDocument Ela(string studentId, string type, double score, string dateIso) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        StudentId = studentId,
        UploadType = type,
        FileName = "test.csv",
        Subject = "ELA",
        Score = score,
        DateIso = dateIso,
    };

    [Fact]
    public void Ignores_Ixl_To_Ilearn_Pairs()
    {
        var assessments = new[]
        {
            Ela("s1", "IXL", 210, "2025-11-11"),
            Ela("s1", "ILEARN", 5432, "2026-03-06"),
        };

        var (avg, count) = DashboardMetrics.ElaSameInstrumentGrowth(assessments);

        Assert.Null(avg);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Averages_Same_Instrument_Deltas()
    {
        var assessments = new[]
        {
            Ela("s1", "IXL", 200, "2025-09-01"),
            Ela("s1", "IXL", 260, "2026-01-15"),
            Ela("s2", "IXL", 100, "2025-09-01"),
            Ela("s2", "IXL", 140, "2026-01-15"),
        };

        var (avg, count) = DashboardMetrics.ElaSameInstrumentGrowth(assessments);

        Assert.Equal(50.0, avg);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Prefers_Ilearn_When_Student_Has_Both_Series()
    {
        var assessments = new[]
        {
            Ela("s1", "IXL", 200, "2025-09-01"),
            Ela("s1", "IXL", 800, "2026-05-01"),
            Ela("s1", "ILEARN", 5400, "2026-02-01"),
            Ela("s1", "ILEARN", 5450, "2026-04-01"),
        };

        var (avg, count) = DashboardMetrics.ElaSameInstrumentGrowth(assessments);

        Assert.Equal(50.0, avg);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Skips_Math_And_Unscored_Rows()
    {
        var assessments = new[]
        {
            Ela("s1", "IXL", 200, "2025-09-01"),
            new AssessmentDocument
            {
                Id = "m", StudentId = "s1", UploadType = "IXL", FileName = "t.csv",
                Subject = "Math", Score = 900, DateIso = "2026-01-01",
            },
            new AssessmentDocument
            {
                Id = "u", StudentId = "s1", UploadType = "IXL", FileName = "t.csv",
                Subject = "ELA", Score = null, DateIso = "2026-02-01",
            },
        };

        var (avg, count) = DashboardMetrics.ElaSameInstrumentGrowth(assessments);

        Assert.Null(avg);
        Assert.Equal(0, count);
    }
}
