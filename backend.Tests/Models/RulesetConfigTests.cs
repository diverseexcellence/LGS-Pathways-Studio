using LgsImpact.Api.Models;
using Newtonsoft.Json;
using Xunit;

namespace LgsImpact.Api.Tests.Models;

public class RulesetConfigTests
{
    // Newtonsoft's default ObjectCreationHandling.Auto MERGES incoming JSON into the property
    // initializer's collection instance rather than replacing it. Every dictionary/list on
    // TierRulesetConfigDocument must carry ObjectCreationHandling.Replace, or an admin "removing"
    // a period weight by omitting it from a PUT would silently keep the code default instead.
    [Fact]
    public void PartialEvidenceWeights_ReplacesRatherThanMerges()
    {
        var partialJson = """{ "evidenceWeights": { "ILEARN": { "CP1": 1.2 } } }""";
        var doc = new TierRulesetConfigDocument();
        JsonConvert.PopulateObject(partialJson, doc);

        Assert.Single(doc.EvidenceWeights["ILEARN"]);
        Assert.Equal(1.2, doc.EvidenceWeights["ILEARN"]["CP1"]);
        Assert.False(doc.EvidenceWeights["ILEARN"].ContainsKey("CP2"),
            "CP2 must be gone, not silently retained from the code default — else 'removing' a weight has no effect.");
    }

    [Fact]
    public void V1Document_DeserializesToWorkingV2Defaults()
    {
        // A pre-existing v1.0 Cosmos document only ever had percentileCutoff/description/etc.
        var v1Json = """{ "rulesetVersion": "1.0", "percentileCutoff": 40, "description": "old rules" }""";
        var doc = JsonConvert.DeserializeObject<TierRulesetConfigDocument>(v1Json)!;

        Assert.Equal("1.0", doc.RulesetVersion); // explicit fields override
        Assert.Equal("old rules", doc.Description);
        // Everything the v1 doc never mentioned falls back to the full v2 default set.
        Assert.NotEmpty(doc.EvidenceWeights);
        Assert.Equal(1.0, doc.EvidenceWeights["ILEARN"]["CP1"]);
        Assert.Equal(2.0, doc.EvidenceWeights["ILEARN"]["CP3"]);
        Assert.Equal(2.5, doc.EvidenceWeights["ILEARN"]["SPRING"]);
        Assert.Contains("IREAD", doc.ExcludedSources);
        Assert.Equal(2, doc.MinDataPoints);
        Assert.Equal(3, doc.TierThresholds.Count);
    }

    [Fact]
    public void DefaultDocument_MatchesSpecValuesExactly()
    {
        var doc = new TierRulesetConfigDocument();

        Assert.Equal(0, doc.CategoryValues["ILEARN"]["below proficiency"]);
        Assert.Equal(1, doc.CategoryValues["ILEARN"]["approaching proficiency"]);
        Assert.Equal(2, doc.CategoryValues["ILEARN"]["at proficiency"]);
        Assert.Equal(3, doc.CategoryValues["ILEARN"]["above proficiency"]);

        Assert.Equal(0, doc.CategoryValues["IXL"]["far below grade"]);
        Assert.Equal(1, doc.CategoryValues["IXL"]["below grade"]);
        Assert.Equal(2, doc.CategoryValues["IXL"]["on grade"]);
        Assert.Equal(3, doc.CategoryValues["IXL"]["above grade"]);

        Assert.Equal(0, doc.CategoryValues["Acadience"]["well below benchmark"]);
        Assert.Equal(1, doc.CategoryValues["Acadience"]["below benchmark"]);
        Assert.Equal(2, doc.CategoryValues["Acadience"]["at benchmark"]);
        Assert.Equal(3, doc.CategoryValues["Acadience"]["above benchmark"]);

        Assert.Equal(1.0, doc.EvidenceWeights["ILEARN"]["CP1"]);
        Assert.Equal(1.5, doc.EvidenceWeights["ILEARN"]["CP2"]);
        Assert.Equal(2.0, doc.EvidenceWeights["ILEARN"]["CP3"]);
        Assert.Equal(2.5, doc.EvidenceWeights["ILEARN"]["SPRING"]);
        Assert.Equal(1.0, doc.EvidenceWeights["IXL"]["BOY"]);
        Assert.Equal(1.5, doc.EvidenceWeights["IXL"]["MOY"]);
        Assert.Equal(2.0, doc.EvidenceWeights["IXL"]["EOY"]);
        Assert.Equal(1.0, doc.EvidenceWeights["Acadience"]["BOY"]);
        Assert.Equal(1.0, doc.EvidenceWeights["Acadience"]["MOY"]);
        Assert.Equal(1.0, doc.EvidenceWeights["Acadience"]["EOY"]);

        Assert.Equal(2, doc.MinDataPoints);
        Assert.Equal("IREAD", Assert.Single(doc.ExcludedSources));
        Assert.Equal("ELA", doc.SourceSubjectOverrides["Acadience"]);
        Assert.Null(doc.UnknownPeriodWeight);
        Assert.False(doc.PercentileFallbackEnabled);
    }
}
