using System.Text.Json;

namespace SdevEng.Tests;

public sealed class TriageClassifierTests
{
    static JsonDocument Catalog(string labels = """
    [
      {"name":"type:chore","family":"type"},
      {"name":"type:bug","family":"type"},
      {"name":"area:tooling","family":"area"},
      {"name":"area:docs","family":"area"}
    ]
    """) => JsonDocument.Parse("{\"labels\":" + labels + "}");

    static IssueTriageFacts Facts(string title, string body = "", params string[] areas) =>
        new("acme/widget", title, body, [], [], [], areas, ["type", "area", "risk", "complexity", "scope"], false);

    [Theory]
    [InlineData("chore: tidy", "", "type:chore")]
    [InlineData("Fix issue", "type:bug", "type:bug")]
    public void ResolvesOnlyExplicitConfiguredTypeEvidence(string title, string body, string expected)
    {
        using var catalog = Catalog();
        var result = TriageClassifier.Classify(Facts(title, body), catalog.RootElement);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(expected, candidate.Label);
        Assert.Equal(1.0, candidate.Confidence);
        Assert.NotEmpty(candidate.Evidence);
        Assert.DoesNotContain("type", result.UnresolvedFamilies);
    }

    [Fact]
    public void ConflictingAndUnconfiguredTypeEvidenceAbstains()
    {
        using var catalog = Catalog();
        var conflict = TriageClassifier.Classify(Facts("chore: fix", "type:bug"), catalog.RootElement);
        Assert.Empty(conflict.Candidates);
        Assert.Contains("type", conflict.UnresolvedFamilies);

        var unconfigured = TriageClassifier.Classify(Facts("feature: new", "type:feature"), catalog.RootElement);
        Assert.Empty(unconfigured.Candidates);
        Assert.Contains("type", unconfigured.UnresolvedFamilies);
    }

    [Fact]
    public void MapsExactAreaHintsToEveryConfiguredMatchWithoutAliases()
    {
        using var catalog = Catalog("""
        [
          {"name":"area:tooling","family":"area"},
          {"name":"area:docs","family":"area"}
        ]
        """);
        var result = TriageClassifier.Classify(Facts("Update", "", "tooling", "area:docs", "tools"), catalog.RootElement);
        Assert.Equal(new[] { "area:docs", "area:tooling" }, result.Candidates.Select(x => x.Label).Order(StringComparer.Ordinal));
        Assert.All(result.Candidates, candidate => Assert.Equal(1.0, candidate.Confidence));
        Assert.DoesNotContain("area", result.UnresolvedFamilies);
        Assert.Contains("risk", result.UnresolvedFamilies);
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Label == "area:tools");
    }

    [Fact]
    public void NoFuzzyKeywordMappingAndEvidenceAreDeterministic()
    {
        using var catalog = Catalog();
        var facts = Facts("Fix parser", "The tooling is broken", "tooling");
        var first = TriageClassifier.Classify(facts, catalog.RootElement);
        var second = TriageClassifier.Classify(facts, catalog.RootElement);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal("area:tooling", Assert.Single(first.Candidates).Label);
        Assert.Equal("area-hint:tooling", Assert.Single(first.Candidates[0].Evidence));
        Assert.Contains("type", first.UnresolvedFamilies);
    }
}
