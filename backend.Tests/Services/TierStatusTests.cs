using LgsImpact.Api.Models;
using LgsImpact.Api.Services;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// "Finalized" was renamed to "Admin Override". Student documents written before the rename still
/// carry the old value, and the don't-recalculate gate is keyed off it — so if the legacy value
/// ever stops being recognised, the engine silently overwrites tiers a person set by hand. These
/// tests exist to make that regression impossible to ship.
/// </summary>
public class TierStatusTests
{
    [Theory]
    [InlineData("Admin Override")]
    [InlineData("Finalized")]           // legacy value, still stored on pre-rename documents
    [InlineData("admin override")]      // status arriving from an older client with different casing
    [InlineData("FINALIZED")]
    public void IsAdminOverride_TreatsCurrentAndLegacyValuesAsOverridden(string status)
        => Assert.True(TierStatus.IsAdminOverride(status));

    [Theory]
    [InlineData("Pending")]
    [InlineData("System Recommended")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAdminOverride_DoesNotLockCalculatedOrPendingSubjects(string? status)
        => Assert.False(TierStatus.IsAdminOverride(status));

    [Fact]
    public void AllSubjectsOverridden_RequiresBothSubjects()
    {
        var student = MakeStudent(TierStatus.AdminOverride, TierStatus.SystemRecommended);
        Assert.False(student.AllSubjectsOverridden);
        Assert.True(student.AnySubjectOverridden);

        student.MathTier.Status = TierStatus.AdminOverride;
        Assert.True(student.AllSubjectsOverridden);
    }

    [Fact]
    public void AllSubjectsOverridden_HonoursALegacyFinalizedSubject()
    {
        // A student finalized before the rename must still be protected from recalculation.
        var student = MakeStudent(TierStatus.LegacyFinalized, TierStatus.LegacyFinalized);
        Assert.True(student.AllSubjectsOverridden);
        Assert.True(student.AnySubjectOverridden);
    }

    [Fact]
    public void AllSubjectsOverridden_HonoursAMixOfLegacyAndCurrentValues()
    {
        var student = MakeStudent(TierStatus.LegacyFinalized, TierStatus.AdminOverride);
        Assert.True(student.AllSubjectsOverridden);
    }

    [Fact]
    public void PendingStudent_IsNeverTreatedAsOverridden()
    {
        var student = MakeStudent(TierStatus.Pending, TierStatus.Pending);
        Assert.False(student.AllSubjectsOverridden);
        Assert.False(student.AnySubjectOverridden);
    }

    [Fact]
    public void LegacyFinalized_IsNotTheValueWeWrite()
    {
        // Guards against someone "simplifying" the constants back into one value.
        Assert.NotEqual(TierStatus.AdminOverride, TierStatus.LegacyFinalized);
        Assert.Equal("Admin Override", TierStatus.AdminOverride);
        Assert.Equal("Finalized", TierStatus.LegacyFinalized);
    }

    private static StudentDocument MakeStudent(string elaStatus, string mathStatus) => new()
    {
        Id = "s1",
        StudentId = "s1",
        ElaTier = new SubjectTier { Status = elaStatus, Tier = "Tier 2" },
        MathTier = new SubjectTier { Status = mathStatus, Tier = "Tier 2" },
    };
}
