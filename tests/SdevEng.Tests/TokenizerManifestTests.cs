using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng.Infrastructure;

public sealed class TokenizerManifestTests
{
    [Fact]
    public void ManifestSchemaAndRegistryAcceptPinnedMetadataAndRejectInvalidAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), "tokenizer-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[] { 1, 3, 3, 7 };
            File.WriteAllBytes(Path.Combine(root, "encoding.fixture"), bytes);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var manifest = new TokenizerManifest(1, "fixture-byte-encoding", "fixture", "fixture-rev-1",
                [new("encoding.fixture", digest)], "fixture-byte-v1", ["<|end|>"], "fixture-v1", true);
            manifest.Validate();
            var json = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/tokenizer-manifest.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid, JsonSerializer.Serialize(json));
            var missing = json.DeepClone(); missing.AsObject().Remove("encoding");
            Assert.False(schema.Evaluate(missing).IsValid);
            var unknown = json.DeepClone(); unknown["family"] = "unknown";
            Assert.False(schema.Evaluate(unknown).IsValid);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TokenizerManifest>(JsonSerializer.Serialize(unknown), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Throws<ArgumentException>(() => (manifest with { SpecialTokens = ["<|end|>", "<|end|>"] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0], manifest.Assets[0]] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0] with { Sha256 = "bad" }] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0] with { Path = "../outside" }] }).Validate());

            var registry = new TokenizerRegistry(root);
            registry.Register(manifest);
            Assert.Equal(JsonSerializer.Serialize(manifest), JsonSerializer.Serialize(registry.Get(manifest.Id)));
            Assert.Throws<ArgumentException>(() => registry.Register(manifest));
            Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
            Assert.Throws<InvalidDataException>(() => new TokenizerRegistry(root).Register(manifest with { Assets = [manifest.Assets[0] with { Sha256 = new string('0', 64) }] }));
            Assert.Throws<FileNotFoundException>(() => new TokenizerRegistry(root).Register(manifest with { Assets = [new("missing.fixture", digest)] }));
            var metadataPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(JsonSerializer.Serialize(manifest), JsonSerializer.Serialize(TokenizerRegistry.ReadMetadata(metadataPath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProductionManifestRequiresPinnedAdapterAndRevision()
    {
        var production = new TokenizerManifest(1, "cl100k-base", "tiktoken", "sha256:" + new string('a', 64),
            [new("tiktoken.tiktoken", new string('b', 64))], "cl100k_base", ["<|endoftext|>"], "tiktoken-v1");
        production.Validate();
        Assert.Throws<ArgumentException>(() => (production with { Revision = "latest" }).Validate());
        Assert.Throws<ArgumentException>(() => (production with { Adapter = "fixture-v1" }).Validate());
    }
}
