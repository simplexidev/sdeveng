using System.IO.Compression;
using System.Security.Cryptography;
using System.Reflection;
using SdevEng.Infrastructure;

public sealed class TiktokenCountingTests
{
    // Independent OpenAI cl100k vectors: https://github.com/openai/tiktoken/blob/main/tests/test_encoding.py
    [Theory]
    [InlineData("", new int[0])]
    [InlineData("hello world", new[] { 15339, 1917 })]
    [InlineData("👍", new[] { 9468, 239, 235 })]
    [InlineData("hello <|endoftext|>", new[] { 15339, 220, 100257 })]
    public void VerifiedProductionVocabularyEncodesKnownVectors(string input, int[] expected)
    {
        using var fixture = new VocabularyFixture(["<|endoftext|>"]);
        var adapter = Assert.IsType<TiktokenTokenizerAdapter>(fixture.Registry.Resolve("cl100k"));
        Assert.Equal(expected, adapter.EncodeToIds(input));
        Assert.Equal(expected.LongLength, adapter.CountTokens(input));
        Assert.False(adapter.Manifest.FixtureOnly);
    }

    [Fact]
    public void SpecialTokensRequireExplicitKnownDeclarationsAndFullVocabulary()
    {
        using var literal = new VocabularyFixture([]);
        Assert.Equal(2, literal.Registry.Resolve("cl100k").CountTokens("hello world"));
        Assert.Throws<NotSupportedException>(() => literal.Registry.Resolve("cl100k").CountTokens("hello <|endoftext|>"));
        using var unsupported = new VocabularyFixture(["<unknown>"]);
        Assert.Throws<NotSupportedException>(() => unsupported.Registry.Resolve("cl100k").CountTokens("hello world"));
        using var otherEncoding = new VocabularyFixture([], "other");
        Assert.Throws<NotSupportedException>(() => otherEncoding.Registry.Resolve("cl100k").CountTokens("hello world"));
    }

    [Fact]
    public void MeasurementPathHasOneCompleteInputEncodingAndNoInferenceDependency()
    {
        var root = AgentTool.FindToolkit();
        var counter = File.ReadAllText(Path.Combine(root, "src/SdevEng.Infrastructure/RenderedInputTokenCounter.cs"));
        Assert.Equal(1, counter.Split("adapter.CountTokens(", StringSplitOptions.None).Length - 1);
        Assert.Contains("adapter.CountTokens(rendering.Text!)", counter);
        var adapter = File.ReadAllText(Path.Combine(root, "src/SdevEng.Infrastructure/TokenizerAdapters.cs"));
        Assert.Contains("TiktokenTokenizer.CreateForModel(\"gpt-4\", stream)", adapter);
        foreach (var source in new[] { counter, adapter })
        {
            Assert.DoesNotContain("HttpClient", source);
            Assert.DoesNotContain("Process.Start", source);
            Assert.DoesNotContain("Generate", source);
        }
    }

    internal static byte[] LoadVocabulary()
    {
        var assembly = Assembly.Load("Microsoft.ML.Tokenizers.Data.Cl100kBase");
        var name = Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith("cl100k_base.tiktoken.deflate", StringComparison.Ordinal));
        using var resource = assembly.GetManifestResourceStream(name)!;
        using var deflate = new DeflateStream(resource, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        Assert.Equal("91423dd5a6f4f288c81534b7608e52cc6d54bc7a4bb522b31e9ed9a9b87061be",
            Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant());
        // The pinned data package stores base64 tokens in rank order, with a capacity header.
        // Expand its compact representation to the upstream token/rank asset, not an encoder.
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal("Capacity: 100257", reader.ReadLine());
        var vocabulary = new System.Text.StringBuilder();
        var rank = 0;
        while (reader.ReadLine() is { } token)
            vocabulary.Append(token).Append(' ').Append((rank++).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        Assert.Equal(100256, rank);
        var bytes = System.Text.Encoding.UTF8.GetBytes(vocabulary.ToString());
        Assert.Equal("223921b76ee99bde995b7ff738513eef100fb51d18c93597a113bcffe865b2a7",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return bytes;
    }

    private sealed class VocabularyFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cl100k-" + Guid.NewGuid().ToString("N"));
        public TokenizerRegistry Registry { get; }
        public VocabularyFixture(string[] specials, string encoding = "cl100k_base")
        {
            Directory.CreateDirectory(_root);
            var bytes = LoadVocabulary();
            File.WriteAllBytes(Path.Combine(_root, "vocab.tiktoken"), bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Registry = new(_root);
            Registry.Register(new(1, "cl100k", "gpt-family", "tiktoken", "sha256:" + hash,
                [new("vocab.tiktoken", hash)], encoding, specials, "tiktoken-v1"));
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
