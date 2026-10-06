using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng.Infrastructure;

public sealed class RequestVariantTokenMeasurerTests
{
    [Fact]
    public void MeasuresBothSuppliedVariantsWithOneVerifiedTokenizerAndEmitsSchemaValidAttribution()
    {
        var root = Path.Combine(Path.GetTempPath(), "request-variants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var asset = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(root, "vocab.bin"), asset);
            var tokenizer = new TokenizerManifest(1, "fixture", "fixture-model", "fixture", "r1",
                [new("vocab.bin", Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant())], "fixture", [], "fixture-v1", true);
            var metadataPath = Path.Combine(root, "tokenizer.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(tokenizer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var registry = new TokenizerRegistry(root);
            registry.RegisterFile(metadataPath);

            var result = new RequestVariantTokenMeasurer(registry).Measure("fixture",
                new("keep alpha and beta", "alpha beta", "alpha", new Dictionary<string, string> { ["alpha"] = "alpha", ["beta"] = "beta" }));

            Assert.Equal(19, result.Original.Tokens);
            Assert.Equal(10, result.Normalized.Tokens);
            Assert.Equal(5, result.Condensed.Tokens);
            Assert.Equal(14, result.RequestTokensSaved);
            Assert.Equal("fixture", result.TokenizerId);
            result.Validate();
            var json = JsonNode.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/request-variant-token-measurement.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid, json.ToJsonString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsRequiredFactMissingFromNormalizedText()
    {
        var root = Path.Combine(Path.GetTempPath(), "request-variants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var asset = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(root, "vocab.bin"), asset);
            var tokenizer = new TokenizerManifest(1, "fixture", "fixture-model", "fixture", "r1",
                [new("vocab.bin", Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant())], "fixture", [], "fixture-v1", true);
            var path = Path.Combine(root, "tokenizer.json");
            File.WriteAllText(path, JsonSerializer.Serialize(tokenizer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var registry = new TokenizerRegistry(root);
            registry.RegisterFile(path);
            Assert.Throws<ArgumentException>(() => new RequestVariantTokenMeasurer(registry).Measure("fixture",
                new("include safeguard", "short request", "short", new Dictionary<string, string> { ["safeguard"] = "safeguard" })));
        }
        finally { Directory.Delete(root, true); }
    }
}
