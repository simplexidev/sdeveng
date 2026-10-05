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
            var manifest = new TokenizerManifest(1, "fixture-byte-encoding", "fixture-model", "fixture", "fixture-rev-1",
                [new("encoding.fixture", digest)], "fixture-byte-v1", ["<|end|>"], "fixture-v1", true);
            manifest.Validate();
            var json = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/tokenizer-manifest.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid, JsonSerializer.Serialize(json));
            var missing = json.DeepClone(); missing.AsObject().Remove("encoding");
            Assert.False(schema.Evaluate(missing).IsValid);
            var missingModelFamily = json.DeepClone(); missingModelFamily.AsObject().Remove("modelFamily");
            Assert.False(schema.Evaluate(missingModelFamily).IsValid);
            var unknown = json.DeepClone(); unknown["family"] = "unknown";
            Assert.False(schema.Evaluate(unknown).IsValid);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TokenizerManifest>(JsonSerializer.Serialize(unknown), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Throws<ArgumentException>(() => (manifest with { SpecialTokens = ["<|end|>", "<|end|>"] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { ModelFamily = " " }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0], manifest.Assets[0]] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0] with { Sha256 = "bad" }] }).Validate());
            Assert.Throws<ArgumentException>(() => (manifest with { Assets = [manifest.Assets[0] with { Path = "../outside" }] }).Validate());

            var registry = new TokenizerRegistry(root);
            var metadataPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var availability = new TokenizerRegistry(root).CheckAvailability(metadataPath);
            Assert.Equal(new TokenizerAvailabilityResult(true, manifest.Id, null), availability);
            Assert.False(File.Exists(Path.Combine(root, "fixture-model.weights")));
            registry.Register(manifest);
            var fixtureAdapter = Assert.IsType<FixtureTokenizerAdapter>(registry.Resolve(manifest.Id));
            Assert.True(fixtureAdapter.Manifest.FixtureOnly);
            Assert.Equal(new byte[] { 1, 3, 7 }, fixtureAdapter.Vocabulary);
            Assert.Equal(JsonSerializer.Serialize(manifest), JsonSerializer.Serialize(registry.Get(manifest.Id)));
            Assert.Throws<ArgumentException>(() => registry.Register(manifest));
            Assert.Throws<KeyNotFoundException>(() => registry.Get("missing"));
            Assert.Throws<InvalidDataException>(() => new TokenizerRegistry(root).Register(manifest with { Assets = [manifest.Assets[0] with { Sha256 = new string('0', 64) }] }));
            Assert.Throws<FileNotFoundException>(() => new TokenizerRegistry(root).Register(manifest with { Assets = [new("missing.fixture", digest)] }));
            var mismatched = manifest with { Assets = [manifest.Assets[0] with { Sha256 = new string('0', 64) }] };
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(mismatched, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var checksumUnavailable = new TokenizerRegistry(root).CheckAvailability(metadataPath);
            Assert.False(checksumUnavailable.Available);
            Assert.Contains("checksum mismatch", checksumUnavailable.Reason);
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(JsonSerializer.Serialize(manifest), JsonSerializer.Serialize(TokenizerRegistry.ReadMetadata(metadataPath)));
            var legacyMetadata = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            legacyMetadata.AsObject().Remove("modelFamily");
            File.WriteAllText(metadataPath, legacyMetadata.ToJsonString());
            Assert.Equal("legacy-unspecified", TokenizerRegistry.ReadMetadata(metadataPath).ModelFamily);
            Assert.Equal("legacy-unspecified", new TokenizerRegistry(root).RegisterFile(metadataPath).Manifest.ModelFamily);
            var unsupported = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            unsupported["adapter"] = "unsupported-adapter";
            File.WriteAllText(metadataPath, unsupported.ToJsonString());
            var unavailable = new TokenizerRegistry(root).CheckAvailability(metadataPath);
            Assert.False(unavailable.Available);
            Assert.Null(unavailable.TokenizerId);
            Assert.Contains("Unknown tokenizer identity", unavailable.Reason);
            File.WriteAllBytes(Path.Combine(root, "encoding.fixture"), [9]);
            Assert.Throws<InvalidDataException>(() => registry.Resolve(manifest.Id));
            File.Delete(Path.Combine(root, "encoding.fixture"));
            Assert.Throws<FileNotFoundException>(() => registry.Resolve(manifest.Id));
            Assert.Throws<KeyNotFoundException>(() => registry.Resolve("unknown"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProductionManifestRequiresPinnedAdapterAndRevision()
    {
        var production = new TokenizerManifest(1, "cl100k-base", "gpt-family", "tiktoken", "sha256:" + new string('a', 64),
            [new("tiktoken.tiktoken", new string('b', 64))], "cl100k_base", ["<|endoftext|>"], "tiktoken-v1");
        production.Validate();
        Assert.Equal("gpt-family", production.ModelFamily);
        Assert.Throws<ArgumentException>(() => (production with { Revision = "latest" }).Validate());
        Assert.Throws<ArgumentException>(() => (production with { Adapter = "fixture-v1" }).Validate());
    }

    [Theory]
    [InlineData("YQ== 0\nYg== 1\n", true)]
    [InlineData("invalid", false)]
    [InlineData("YQ== 0\nYQ== 1\n", false)]
    [InlineData("YQ== 1\n", false)]
    [InlineData("YQ== 0\nYg== 0\n", false)]
    [InlineData("", false)]
    public void RegistryDispatchesPinnedProductionFormatAndRejectsInvalidVocabulary(string content, bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "tokenizer-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(Path.Combine(root, "vocabulary.tiktoken"), bytes);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var manifest = new TokenizerManifest(1, "production", "gpt-family", "tiktoken", "sha256:" + digest,
                [new("vocabulary.tiktoken", digest)], "cl100k_base", ["<|endoftext|>"], "tiktoken-v1");
            var path = Path.Combine(root, "manifest.json");
            var json = JsonSerializer.SerializeToNode(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/tokenizer-manifest.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid);
            File.WriteAllText(path, json.ToJsonString());
            var registry = new TokenizerRegistry(root);
            if (valid)
            {
                var adapter = Assert.IsType<TiktokenTokenizerAdapter>(registry.RegisterFile(path));
                Assert.Same(adapter, registry.Resolve(manifest.Id));
                Assert.False(adapter.Manifest.FixtureOnly);
                Assert.Equal(0, adapter.MergeableRanks["YQ=="]);
                Assert.Equal(1, adapter.MergeableRanks["Yg=="]);
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => registry.RegisterFile(path));
                Assert.Throws<KeyNotFoundException>(() => registry.Resolve(manifest.Id));
            }
            Assert.Throws<ArgumentException>(() => new TokenizerRegistry(root).Register(manifest with { Revision = "sha256:" + new string('0', 64) }));
            Assert.Throws<ArgumentException>(() => new TokenizerRegistry(root).Register(manifest with { Adapter = "unknown" }));
            Assert.Throws<ArgumentException>(() => new TokenizerRegistry(root).Register(manifest with { Assets = [manifest.Assets[0], new("other", digest)] }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
