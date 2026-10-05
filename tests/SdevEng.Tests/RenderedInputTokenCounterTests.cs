using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng.Infrastructure;

public sealed class RenderedInputTokenCounterTests
{
    [Theory]
    [InlineData("compact", "<assistant>", 270, 298)]
    [InlineData("separated", "\n<m>assistant|", 266, 301)]
    public void CountsCompleteGoldenInputIncludingCrossMessageTokensAndPersistsAttribution(string golden, string prefix, long tokens, long bytes)
    {
        using var fixture = new Fixture(prefix);
        var originalPrompt = JsonSerializer.Serialize(fixture.Prompt);
        var originalText = fixture.Text.ToArray();
        var result = fixture.Count();
        Assert.Equal("exact", result.MeasurementKind);
        Assert.Equal("fixture-byte-v1", result.Method);
        Assert.True(result.FixtureOnly);
        Assert.Equal(tokens, result.Tokens);
        Assert.Equal(bytes, result.Utf8Bytes);
        var goldenBytes = File.ReadAllBytes(Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/chat-templates", golden + ".golden.txt"));
        Assert.Equal(Hash(goldenBytes), result.RenderedInputDigest);
        Assert.Equal(fixture.Template.Id, result.TemplateId);
        Assert.Equal(fixture.Template.Revision, result.TemplateRevision);
        Assert.Equal(fixture.Template.Checksum, result.TemplateChecksum);
        Assert.Equal(fixture.Tokenizer.Id, result.TokenizerId);
        Assert.Equal(fixture.Tokenizer.Revision, result.TokenizerRevision);
        Assert.Equal(fixture.Tokenizer.Assets, result.TokenizerAssets);
        // The fixture special spans end + separator + start, so fragment sums cannot yield this count.
        Assert.True(result.Tokens < result.Utf8Bytes);
        var reordered = fixture.Prompt with
        {
            Components = fixture.Prompt.Components.Select(c => c.Id == PromptComponentId.Tools
            ? c with { Tools = c.Tools!.Reverse().ToArray() } : c).ToArray()
        };
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(fixture.Count(prompt: reordered)));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(fixture.Count()));
        fixture.AssertArtifact(result);
        fixture.Text["s"] = "é🌍";
        Assert.Equal(tokens + 3, fixture.Count().Tokens);
        fixture.Text["s"] = "sys";
        Assert.Equal(originalPrompt, JsonSerializer.Serialize(fixture.Prompt));
        Assert.Equal(originalText, fixture.Text.ToArray());
        Assert.Equal(fixture.Asset, File.ReadAllBytes(fixture.AssetPath));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(fixture.Root, "unrelated.txt")));
    }

    [Fact]
    public void AbsentInvalidAndTamperedInputFailClosedAndReplayAfterRestoration()
    {
        using var fixture = new Fixture("<assistant>");
        var original = fixture.Count();
        var missingTemplate = fixture.Counter.Count("absent", "r1", fixture.Template.Checksum, fixture.Prompt, fixture.Text);
        AssertUnavailable(fixture, missingTemplate, "chat-template-unavailable");
        Assert.Null(missingTemplate.RenderedInputDigest);
        fixture.Text.Remove("q");
        AssertUnavailable(fixture, fixture.Count(new(false, true)), "prompt-content-unavailable");
        fixture.Text["q"] = "ask";
        var invalid = fixture.Prompt with { Components = fixture.Prompt.Components.Reverse().ToArray() };
        AssertUnavailable(fixture, fixture.Count(prompt: invalid), "chat-template-or-tokenizer-unavailable");
        File.WriteAllBytes(fixture.AssetPath, [0]);
        var tampered = fixture.Count(new(false, true));
        AssertUnavailable(fixture, tampered, "tokenizer-unavailable");
        Assert.Equal(fixture.Tokenizer.Assets, tampered.TokenizerAssets);
        File.Delete(fixture.AssetPath);
        AssertUnavailable(fixture, fixture.Count(), "tokenizer-unavailable");
        File.WriteAllBytes(fixture.AssetPath, fixture.Asset);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(fixture.Count()));
        var bad = original with { MeasurementKind = "estimated", Method = "ceil-utf8-bytes-div-4", Tokens = 1 };
        Assert.Throws<ArgumentException>(() => Artifacts.WriteRenderedInputTokenMeasurement(Path.Combine(fixture.Root, "bad.json"), bad));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "bad.json")));
        var json = JsonSerializer.SerializeToNode(original, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        json["fixtureOnly"] = false;
        Assert.False(fixture.Schema.Evaluate(json).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedEncodingRequiresExplicitEstimateAndNeverSatisfiesExactPolicy(bool productionFormat)
    {
        using var fixture = new Fixture("<assistant>", productionFormat, asciiOnly: true);
        fixture.Text["s"] = "é🌍";
        AssertUnavailable(fixture, fixture.Count(), "exact-tokenization-unavailable");
        AssertUnavailable(fixture, fixture.Count(new(RequireExact: true, AllowEstimate: true)), "exact-tokenization-unavailable");
        AssertUnavailable(fixture, fixture.Count(new(RequireExact: false)), "exact-tokenization-unavailable");
        var estimate = fixture.Count(new(RequireExact: false, AllowEstimate: true));
        Assert.Equal("estimated", estimate.MeasurementKind);
        Assert.Equal("ceil-utf8-bytes-div-4", estimate.Method);
        Assert.Equal(301L, estimate.Utf8Bytes);
        Assert.Equal(76L, estimate.Tokens);
        Assert.Equal(!productionFormat, estimate.FixtureOnly);
        fixture.AssertArtifact(estimate);
        Assert.Equal(JsonSerializer.Serialize(estimate), JsonSerializer.Serialize(fixture.Count(new(false, true))));
    }

    private static void AssertUnavailable(Fixture fixture, RenderedInputTokenMeasurement result, string reason)
    {
        Assert.Equal("unavailable", result.MeasurementKind);
        Assert.Null(result.Tokens);
        Assert.Null(result.Method);
        Assert.Equal(reason, result.UnavailableReason);
        fixture.AssertArtifact(result);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "full-input-counter-" + Guid.NewGuid().ToString("N"));
        public string AssetPath => Path.Combine(Root, "vocab.bin");
        public byte[] Asset { get; }
        public TokenizerManifest Tokenizer { get; }
        public ChatTemplateManifest Template { get; }
        public IRenderedInputTokenCounter Counter { get; }
        public JsonSchema Schema { get; } = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/rendered-input-token-measurement.schema.json"));
        public Dictionary<string, string> Text { get; } = new() { ["s"] = "sys", ["r"] = "role", ["q"] = "ask", ["o"] = "json" };
        public PromptManifest Prompt { get; } = new(1,
        [
            new(PromptComponentId.System, "system", "test", "s", "h"),
            new(PromptComponentId.Role, "assistant", "test", "r", "h"),
            new(PromptComponentId.Request, "user", "test", "q", "h"),
            new(PromptComponentId.Tools, "tools", "test", "tools", "h", Tools: [new("z", "Z", "desc", "z-schema"), new("a", "A", "desc", "a-schema")]),
            new(PromptComponentId.OutputContract, "assistant", "test", "o", "h", ResponseSchema: "schema")
        ]);

        public Fixture(string prefix, bool productionFormat = false, bool asciiOnly = false)
        {
            Directory.CreateDirectory(Root);
            Asset = productionFormat ? Encoding.UTF8.GetBytes("YQ== 0\nYg== 1\n") : Enumerable.Range(0, asciiOnly ? 128 : 256).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(AssetPath, Asset);
            File.WriteAllText(Path.Combine(Root, "unrelated.txt"), "unrelated");
            var digest = Hash(Asset);
            Tokenizer = new(1, "tok", productionFormat ? "gpt-family" : "fixture-model", productionFormat ? "tiktoken" : "fixture",
                productionFormat ? "sha256:" + digest : "r1", [new("vocab.bin", digest)], productionFormat ? "cl100k_base" : "fixture",
                ["</m>\n<m>"], productionFormat ? "tiktoken-v1" : "fixture-v1", !productionFormat);
            var tokenizers = new TokenizerRegistry(Root);
            var tokenizerPath = Path.Combine(Root, "tokenizer.json");
            File.WriteAllText(tokenizerPath, JsonSerializer.Serialize(Tokenizer, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            tokenizers.RegisterFile(tokenizerPath);
            Template = new(1, "template", "r1", new string('0', 64), "tok", Tokenizer.Revision, productionFormat ? "canonical-v1" : "fixture",
                "<m>", "|", "</m>", "\n", "<tools>", "</tools>", prefix, !productionFormat);
            Template = Template with { Checksum = Template.ComputeChecksum() };
            var templates = new ChatTemplateRegistry(tokenizers);
            var templatePath = Path.Combine(Root, "template.json");
            File.WriteAllText(templatePath, JsonSerializer.Serialize(Template, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            templates.RegisterFile(templatePath);
            Counter = new RenderedInputTokenCounter(templates);
        }

        public RenderedInputTokenMeasurement Count(RenderedInputTokenPolicy? policy = null, PromptManifest? prompt = null) =>
            Counter.Count(Template.Id, Template.Revision, Template.Checksum, prompt ?? Prompt, Text, policy);

        public void AssertArtifact(RenderedInputTokenMeasurement result)
        {
            result.Validate();
            var path = Artifacts.WriteRenderedInputTokenMeasurement(Path.Combine(Root, "measurement.json"), result);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(Schema.Evaluate(json).IsValid, json.ToJsonString());
            var replay = json.Deserialize<RenderedInputTokenMeasurement>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            replay.Validate();
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(replay));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
