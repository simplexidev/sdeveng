using System.Text.Json;

namespace SdevEng.Tests;

public sealed class TriageClassifierTests
{
    static JsonDocument Catalog(string labels = """
    [
      {"name":"type:chore","family":"type"},
      {"name":"type:bug","family":"type"},
      {"name":"area:tooling","family":"area"},
      {"name":"area:docs","family":"area"},
      {"name":"scope:single-repo","family":"scope"},
      {"name":"scope:cross-repo","family":"scope"}
    ]
    """) => JsonDocument.Parse("{\"labels\":" + labels + "}");

    static IssueTriageFacts Facts(string title, string body = "", params string[] areas) =>
        new("acme/widget", title, body, [], [], [], areas, ["type", "area", "risk", "complexity", "scope"], false);

    [Fact]
    public void ResolvesScopeFromExplicitLinkedRepositoryReferences()
    {
        using var catalog = Catalog();
        var local = Facts("Update") with { LinkedReferences = [new("acme", "widget", 2, "issue")] };
        var localResult = TriageClassifier.Classify(local, catalog.RootElement);
        Assert.Equal("scope:single-repo", Assert.Single(localResult.Candidates, candidate => candidate.Family == "scope").Label);
        Assert.DoesNotContain("scope", localResult.UnresolvedFamilies);

        var cross = Facts("Update") with { LinkedReferences = [new("acme", "other", 3, "pull")] };
        var crossCandidate = Assert.Single(TriageClassifier.Classify(cross, catalog.RootElement).Candidates, candidate => candidate.Family == "scope");
        Assert.Equal("scope:cross-repo", crossCandidate.Label);
        Assert.Equal(1.0, crossCandidate.Confidence);
    }

    [Fact]
    public void LeavesUnknownOrUnconfiguredScopeUnresolved()
    {
        using var catalog = Catalog();
        var unknown = Facts("Update") with { CandidateRepository = null };
        Assert.Contains("scope", TriageClassifier.Classify(unknown, catalog.RootElement).UnresolvedFamilies);
        using var missing = Catalog("[{\"name\":\"type:bug\",\"family\":\"type\"}]");
        Assert.Contains("scope", TriageClassifier.Classify(Facts("Update"), missing.RootElement).UnresolvedFamilies);
    }

    [Fact]
    public void SemanticDefaultAbstainsAndSemanticCandidatesAreBoundToCatalog()
    {
        using var catalog = Catalog();
        var semantic = new AbstainingTriageSemanticClassifier();
        Assert.Empty(semantic.Classify(Facts("Update"), [], catalog.RootElement));
        var valid = new TriageLabelCandidate("type", "type:bug", 0.8, Enumerable.Repeat(new string('x', 400), 12).ToArray());
        var filtered = TriageClassifier.BoundSemanticCandidates([valid, new("type", "type:made-up", 1, ["guess"]), new("type", "type:bug", double.NaN, [])], catalog.RootElement);
        var candidate = Assert.Single(filtered);
        Assert.Equal(10, candidate.Evidence.Count);
        Assert.All(candidate.Evidence, evidence => Assert.Equal(300, evidence.Length));
    }

    [Fact]
    public void ProductionDiResolvesAbstainingSemanticClassifier()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.IsType<AbstainingTriageSemanticClassifier>(provider.GetService(typeof(ITriageSemanticClassifier)));
        using var catalog = Catalog();
        var decision = TriageClassifier.ClassifyDecision(Facts("chore: tidy"), catalog.RootElement,
            (ITriageSemanticClassifier)provider.GetService(typeof(ITriageSemanticClassifier))!);
        Assert.Contains(decision.Selected, family => family.Family == "type");
        Assert.True(decision.NeedsHumanReview);
    }

    [Fact]
    public void CombinerAppliesConfidenceBoundaryAndRoutesLowConfidenceToReview()
    {
        using var catalog = Catalog();
        var decision = TriageClassifier.Combine([], [
            new("type", "type:bug", 0.80, ["boundary"]),
            new("area", "area:docs", 0.799, ["too-low"])
        ], [], catalog.RootElement);
        Assert.Equal("type:bug", Assert.Single(Assert.Single(decision.Selected, item => item.Family == "type").Labels).Label);
        Assert.DoesNotContain(decision.Selected, item => item.Family == "area");
        Assert.Contains("area", decision.UnresolvedFamilies);
        Assert.True(decision.NeedsHumanReview);
    }

    [Fact]
    public void CombinerLeavesExclusiveConflictsUnresolvedAndAllowsMultipleAreas()
    {
        using var catalog = Catalog();
        var decision = TriageClassifier.Combine([
            new("type", "type:bug", 1, ["one"]), new("type", "type:chore", 1, ["two"]),
            new("area", "area:docs", 1, ["docs"]), new("area", "area:tooling", 1, ["tools"])
        ], [], [], catalog.RootElement);
        Assert.Contains("type", decision.UnresolvedFamilies);
        Assert.DoesNotContain(decision.Selected, item => item.Family == "type");
        Assert.Equal(2, Assert.Single(decision.Selected, item => item.Family == "area").Labels.Count);
        Assert.True(decision.NeedsHumanReview);
    }

    [Fact]
    public void CombinerRejectsUnconfiguredAndFamilyMismatchedCandidates()
    {
        using var catalog = Catalog();
        var decision = TriageClassifier.Combine([], [
            new("type", "type:made-up", 1, ["unknown"]), new("area", "type:bug", 1, ["wrong-family"])
        ], [], catalog.RootElement);
        Assert.DoesNotContain(decision.Selected, item => item.Family is "type" or "area");
        Assert.Contains("type", decision.UnresolvedFamilies);
        Assert.Contains("area", decision.UnresolvedFamilies);
        Assert.True(decision.NeedsHumanReview);
    }

    [Theory]
    [InlineData("chore: tidy", "", "type:chore")]
    [InlineData("Fix issue", "type:bug", "type:bug")]
    public void ResolvesOnlyExplicitConfiguredTypeEvidence(string title, string body, string expected)
    {
        using var catalog = Catalog();
        var result = TriageClassifier.Classify(Facts(title, body), catalog.RootElement);
        var candidate = Assert.Single(result.Candidates, candidate => candidate.Family == "type");
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
        Assert.DoesNotContain(conflict.Candidates, candidate => candidate.Family == "type");
        Assert.Contains("type", conflict.UnresolvedFamilies);

        var unconfigured = TriageClassifier.Classify(Facts("feature: new", "type:feature"), catalog.RootElement);
        Assert.DoesNotContain(unconfigured.Candidates, candidate => candidate.Family == "type");
        Assert.Contains("type", unconfigured.UnresolvedFamilies);
    }

    [Theory]
    [InlineData("risk:low", "risk", "risk:low")]
    [InlineData("risk:medium", "risk", "risk:medium")]
    [InlineData("risk:high", "risk", "risk:high")]
    [InlineData("complexity:low", "complexity", "complexity:low")]
    [InlineData("complexity:medium", "complexity", "complexity:medium")]
    [InlineData("complexity:high", "complexity", "complexity:high")]
    public void ResolvesExplicitConfiguredRiskAndComplexityTokens(string token, string family, string expected)
    {
        using var catalog = Catalog("""
        [
          {"name":"risk:low","family":"risk"}, {"name":"risk:medium","family":"risk"}, {"name":"risk:high","family":"risk"},
          {"name":"complexity:low","family":"complexity"}, {"name":"complexity:medium","family":"complexity"}, {"name":"complexity:high","family":"complexity"}
        ]
        """);
        var result = TriageClassifier.Classify(Facts("Change " + token.ToUpperInvariant()), catalog.RootElement);
        var candidate = Assert.Single(result.Candidates, item => item.Family == family);
        Assert.Equal(expected, candidate.Label);
        Assert.Equal(1.0, candidate.Confidence);
        Assert.Equal(token.ToUpperInvariant(), Assert.Single(candidate.Evidence));
        Assert.DoesNotContain(family, result.UnresolvedFamilies);
    }

    [Theory]
    [InlineData("risk:extreme")]
    [InlineData("complexity:trivial")]
    [InlineData("high risk")]
    [InlineData("complexity is high")]
    [InlineData("x-risk:high")]
    [InlineData("risk:high-extra")]
    public void LeavesUnconfiguredOrNonTokenDeclarationsUnresolved(string text)
    {
        using var catalog = Catalog("""
        [
          {"name":"risk:low","family":"risk"}, {"name":"risk:high","family":"risk"},
          {"name":"complexity:low","family":"complexity"}, {"name":"complexity:high","family":"complexity"}
        ]
        """);
        var result = TriageClassifier.Classify(Facts(text), catalog.RootElement);
        Assert.DoesNotContain(result.Candidates, item => item.Family is "risk" or "complexity");
        Assert.Contains("risk", result.UnresolvedFamilies);
        Assert.Contains("complexity", result.UnresolvedFamilies);
    }

    [Theory]
    [InlineData("risk:low risk:high", "risk")]
    [InlineData("complexity:low complexity:high", "complexity")]
    public void ConflictingConfiguredDeclarationsRemainUnresolved(string text, string family)
    {
        using var catalog = Catalog("""
        [
          {"name":"risk:low","family":"risk"}, {"name":"risk:high","family":"risk"},
          {"name":"complexity:low","family":"complexity"}, {"name":"complexity:high","family":"complexity"}
        ]
        """);
        var result = TriageClassifier.Classify(Facts(text), catalog.RootElement);
        Assert.DoesNotContain(result.Candidates, item => item.Family == family);
        Assert.Contains(family, result.UnresolvedFamilies);
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
        var area = Assert.Single(first.Candidates, candidate => candidate.Family == "area");
        Assert.Equal("area:tooling", area.Label);
        Assert.Equal("area-hint:tooling", Assert.Single(area.Evidence));
        Assert.Contains("type", first.UnresolvedFamilies);
    }
}
