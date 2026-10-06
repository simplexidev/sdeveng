using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng.Infrastructure;

public sealed class EvidenceEfficiencyMeasurerTests
{
    [Fact]
    public void CountsUniqueCandidateAndSelectedEvidenceAndEmitsVersionedSchemaResult()
    {
        using var fixture = new Fixture();
        var input = new[] { new EvidenceEfficiencyItem("a", "rev1", "alpha", null, null, "repo:src/a.cs", "repo:src/a.cs#A"),
            new EvidenceEfficiencyItem("a", "rev1", "alpha", null, null, "repo:src/a.cs", "repo:src/a.cs#A"),
            new EvidenceEfficiencyItem("b", "rev2", "β", null, null, "repo:src/a.cs", "repo:src/a.cs#B") };
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure("fixture", input, ["b"], "coder", 10, 4);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(1, result.CandidateFileCount);
        Assert.Equal(1, result.SelectedFileCount);
        Assert.Equal(2, result.CandidateSymbolCount);
        Assert.Equal(1, result.SelectedSymbolCount);
        Assert.Equal(7, result.CandidateBytes);
        Assert.Equal(7, result.CandidateTokens);
        Assert.Equal(2, result.SelectedBytes);
        Assert.Equal(2, result.SelectedTokens);
        Assert.Equal(2m / 7m, result.SelectionRatio);
        Assert.Equal(40m, result.UtilizationPercent);
        result.Validate();
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-efficiency-measurement.schema.json"));
        Assert.True(schema.Evaluate(json).IsValid, json.ToJsonString());
    }

    [Fact]
    public void CountsDistinctLocationsAcrossDifferentEvidenceIds()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new("one", "rev", "x", 1, 1, "repo:a.cs", "repo:a.cs#M"),
             new("two", "rev", "y", 1, 1, "repo:a.cs", "repo:a.cs#M"),
             new("three", "rev", "z", 1, 1, "repo:b.cs", "repo:b.cs#N")],
            ["two", "three"], "coder", 10, 2);

        Assert.Equal(2, result.CandidateFileCount);
        Assert.Equal(2, result.SelectedFileCount);
        Assert.Equal(2, result.CandidateSymbolCount);
        Assert.Equal(2, result.SelectedSymbolCount);
        result.Validate();
    }

    [Fact]
    public void KeepsUnavailableTokensNullAndZeroDenominatorsUnavailable()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new EvidenceEfficiencyItem("empty", "rev1", "", null, null)], ["empty"], "coder", 0, null);
        Assert.Null(result.CandidateTokens);
        Assert.Null(result.SelectedTokens);
        Assert.Null(result.SelectionRatio);
        Assert.Equal("token-measurement-unavailable", result.SelectionRatioUnavailableReason);
        Assert.Null(result.UtilizationPercent);
        Assert.Equal("role-input-budget-zero", result.UtilizationUnavailableReason);
        result.Validate();
    }

    [Fact]
    public void ReturnsNullRatioWhenMeasuredCandidateTokenTotalIsZero()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new EvidenceEfficiencyItem("empty", "rev1", "", 0, 0)], ["empty"], "coder", 5, 0);

        Assert.Null(result.SelectionRatio);
        Assert.Equal("candidate-tokens-zero", result.SelectionRatioUnavailableReason);
        result.Validate();
    }

    [Fact]
    public void DoesNotTreatUnavailableCandidateMeasurementAsZero()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new EvidenceEfficiencyItem("known", "rev1", "x", null, 2),
             new EvidenceEfficiencyItem("unknown", "rev2", "yy", null, null)], ["unknown"], "coder", 5, null);

        Assert.Equal(3, result.CandidateBytes);
        Assert.Null(result.CandidateTokens);
        Assert.Null(result.SelectedTokens);
        Assert.Equal(2, result.SelectedBytes);
        Assert.Null(result.SelectionRatio);
        Assert.Equal("token-measurement-unavailable", result.SelectionRatioUnavailableReason);
        result.Validate();
    }

    [Fact]
    public void RejectsDuplicateIdWithConflictingMeasuredTokens()
    {
        using var fixture = new Fixture();
        var input = new[] { new EvidenceEfficiencyItem("a", "rev1", "x", null, 1),
            new EvidenceEfficiencyItem("a", "rev1", "x", null, 2) };

        Assert.Throws<ArgumentException>(() => new EvidenceEfficiencyMeasurer(fixture.Registry)
            .Measure(null, input, ["a"], "coder", 5, 1));
    }

    [Fact]
    public void RecordsAttributableTruncationAndOverflowEventsInVersionedResult()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new("a", "rev1", "short", 5, 2)], ["a"], "coder", 5, 6,
            [new("truncation", "a", "rev1", "evidence-token-limit", 12, 5, 6, 2)],
            [new("overflow", "a", "rev1", "mandatory-evidence-exceeded-budget", 5, 5, 2, 2)]);

        Assert.Equal(2, result.SchemaVersion);
        Assert.Single(result.TruncationEvents);
        Assert.Equal(12, result.TruncationEvents[0].OriginalBytes);
        Assert.Equal("mandatory-evidence-exceeded-budget", result.OverflowEvents[0].Reason);
        result.Validate();
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-efficiency-measurement.schema.json"));
        Assert.True(schema.Evaluate(json).IsValid, json.ToJsonString());
    }

    [Fact]
    public void RejectsObservationEventWithMismatchedSourceRevision()
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new("a", "rev1", "short", 5, 2)], ["a"], "coder", 5, 5,
            [new("truncation", "a", "rev2", "limit", 8, 5)]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "evidence-efficiency-" + Guid.NewGuid().ToString("N"));
        public TokenizerRegistry Registry { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var asset = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            File.WriteAllBytes(Path.Combine(_root, "vocab.bin"), asset);
            var manifest = new TokenizerManifest(1, "fixture", "fixture-model", "fixture", "r1",
                [new("vocab.bin", Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant())], "fixture", [], "fixture-v1", true);
            var path = Path.Combine(_root, "tokenizer.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Registry = new TokenizerRegistry(_root);
            Registry.RegisterFile(path);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
