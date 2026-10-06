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
        var input = new[] { new EvidenceEfficiencyItem("a", "rev1", "alpha", null, null),
            new EvidenceEfficiencyItem("a", "rev1", "alpha", null, null), new EvidenceEfficiencyItem("b", "rev2", "β", null, null) };
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure("fixture", input, ["b"], "coder", 10, 4);
        Assert.Equal(2, result.Candidates.Count);
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
    public void KeepsUnavailableTokensNullAndZeroDenominatorsUnavailable()
    {
        using var fixture = new Fixture();
        var result = new EvidenceEfficiencyMeasurer(fixture.Registry).Measure(null,
            [new EvidenceEfficiencyItem("empty", "rev1", "", null, null)], ["empty"], "coder", 0, null);
        Assert.Null(result.CandidateTokens);
        Assert.Null(result.SelectedTokens);
        Assert.Null(result.SelectionRatio);
        Assert.Equal("candidate-bytes-zero", result.SelectionRatioUnavailableReason);
        Assert.Null(result.UtilizationPercent);
        Assert.Equal("role-input-budget-zero", result.UtilizationUnavailableReason);
        result.Validate();
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
