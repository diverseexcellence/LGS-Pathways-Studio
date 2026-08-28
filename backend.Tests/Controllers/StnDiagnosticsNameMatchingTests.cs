using LgsImpact.Api.Controllers;
using Xunit;

namespace LgsImpact.Api.Tests.Controllers;

// Covers the name-comparison helpers behind GET /api/upload/stn-diagnostics
// (UploadController.DiagNormalizeName / IsTokenSubsetMatch / ClassifyNameMismatch / Fingerprint).
// These are diagnostic-only — separate from, and less conservative than, the production
// matching logic in ProcessRowsAsync — but the one property that must hold regardless is that
// they never treat two different students as the same name. That's the load-bearing half of
// this suite: the sibling pairs in the original missing-STN report must stay distinct.
public class StnDiagnosticsNameMatchingTests
{
    [Theory]
    [InlineData("Ja’mir Hoskins", "Ja'mir Hoskins")]      // curly apostrophe vs straight
    [InlineData("Za‘Mir Williams", "Za'Mir Williams")]    // alternate curly form
    [InlineData("MADISON DANIELS", "Madison Daniels")]         // case
    [InlineData("Mabell  Riera Diaz", "Mabell Riera Diaz")]    // double space
    [InlineData("Alejandra Prado-González", "Alejandra Prado-Gonzalez")] // accent
    [InlineData("Wyatt Shepard ", " Wyatt Shepard")]           // leading/trailing whitespace
    public void DiagNormalizeName_TreatsNearMissSpellingsAsEqual(string a, string b)
    {
        Assert.Equal(UploadController.DiagNormalizeName(a), UploadController.DiagNormalizeName(b));
    }

    [Theory]
    [InlineData("Rydur Shaw", "Rylin Shaw")]
    [InlineData("Za'Mir Williams", "Ya'mila Williams")]
    [InlineData("Ja'mir Hoskins", "Jalecia Hoskins")]
    [InlineData("Ya'mila Williams", "Yalaisha Williams")]
    // Apostrophes are form-normalized (curly -> straight), never stripped — this is the
    // guardrail that keeps "Ja'mir" from silently collapsing onto a differently-spelled "Jamir".
    [InlineData("Ja'mir Hoskins", "Jamir Hoskins")]
    [InlineData("Haley Brooks", "Haleigh Brooks")]
    public void DiagNormalizeName_NeverMergesDifferentStudents(string a, string b)
    {
        Assert.NotEqual(UploadController.DiagNormalizeName(a), UploadController.DiagNormalizeName(b));
    }

    [Fact]
    public void IsTokenSubsetMatch_DetectsExtraMiddleNameOnly()
    {
        Assert.True(UploadController.IsTokenSubsetMatch("Keiry Marili Melendez Portillo", "Keiry Melendez Portillo"));
        Assert.True(UploadController.IsTokenSubsetMatch("Keiry Melendez Portillo", "Keiry Marili Melendez Portillo"));
    }

    [Theory]
    [InlineData("Rydur Shaw", "Rylin Shaw")]         // same length, different first name
    [InlineData("Za'Mir Williams", "Ya'mila Williams")]
    public void IsTokenSubsetMatch_RejectsSameLengthDifferentNames(string a, string b)
    {
        Assert.False(UploadController.IsTokenSubsetMatch(a, b));
    }

    [Fact]
    public void IsTokenSubsetMatch_RequiresMatchingFirstAndLastToken()
    {
        // Extra token present, but first name differs — must not match.
        Assert.False(UploadController.IsTokenSubsetMatch("Keiry Marili Melendez Portillo", "Janyiah Melendez Portillo"));
    }

    [Theory]
    [InlineData("Za’Mir Williams", "Za'Mir Williams", "ApostropheForm")]
    [InlineData("Mabell  Riera Diaz", "Mabell Riera Diaz", "Whitespace")]
    [InlineData("Alejandra Prado-González", "Alejandra Prado-Gonzalez", "Accent")]
    public void ClassifyNameMismatch_LabelsTheSpecificDifference(string source, string roster, string expectedKind)
    {
        Assert.Equal(expectedKind, UploadController.ClassifyNameMismatch(source, roster));
    }

    [Fact]
    public void ClassifyNameMismatch_AlreadyOrdinalEqualIsAlreadyMatchable()
    {
        // Same case-insensitive string — today's production comparison would already succeed,
        // so a mismatch here points at a header/upload-type gap, not a spelling issue.
        Assert.Equal("AlreadyMatchable", UploadController.ClassifyNameMismatch("madison daniels", "Madison Daniels"));
    }

    [Fact]
    public void Fingerprint_IsStableAndNeverEqualsTheInput()
    {
        var a = UploadController.Fingerprint("Ja'mir Hoskins", "salt");
        var b = UploadController.Fingerprint("Ja’mir Hoskins", "salt"); // curly variant, same normalized name
        Assert.Equal(a, b);
        Assert.NotEqual("Ja'mir Hoskins", a);
    }

    [Fact]
    public void Fingerprint_DistinctForDistinctSiblingNames()
    {
        var rydur = UploadController.Fingerprint("Rydur Shaw", "salt");
        var rylin = UploadController.Fingerprint("Rylin Shaw", "salt");
        Assert.NotEqual(rydur, rylin);
    }

    [Fact]
    public void ExtractRowNameForDiagnostics_HandlesLastCommaFirstFormat()
    {
        var row = new Dictionary<string, string> { ["Student Name"] = "Hoskins, Ja'mir" };
        Assert.Equal("Ja'mir Hoskins", UploadController.ExtractRowNameForDiagnostics(row));
    }

    [Fact]
    public void ExtractRowNameForDiagnostics_BuildsFromFirstAndLastColumns()
    {
        var row = new Dictionary<string, string> { ["First Name"] = "Rylin", ["Last Name"] = "Shaw" };
        Assert.Equal("Rylin Shaw", UploadController.ExtractRowNameForDiagnostics(row));
    }
}
