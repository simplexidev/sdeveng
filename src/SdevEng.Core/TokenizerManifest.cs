using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdevEng;

public static class TokenizerAssetFamily
{
    public const string Fixture = "fixture";
    public const string Tiktoken = "tiktoken";
}

public static class TokenizerAdapterId
{
    public const string Fixture = "fixture-v1";
    public const string Tiktoken = "tiktoken-v1";
}

/// <summary>Versioned metadata for an exact tokenizer asset, separate from model weights.</summary>
public sealed record TokenizerManifest(
    int SchemaVersion,
    string Id,
    [property: JsonConverter(typeof(TokenizerAssetFamilyJsonConverter))] string Family,
    string Revision,
    IReadOnlyList<TokenizerAsset> Assets,
    string Encoding,
    IReadOnlyList<string> SpecialTokens,
    [property: JsonConverter(typeof(TokenizerAdapterIdJsonConverter))] string Adapter,
    bool FixtureOnly = false)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentException("Unsupported tokenizer manifest schema version.");
        Require(Id, nameof(Id)); Require(Revision, nameof(Revision)); Require(Encoding, nameof(Encoding));
        if (Assets is null || Assets.Count == 0) throw new ArgumentException("At least one tokenizer asset is required.", nameof(Assets));
        if (Assets.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count() != Assets.Count) throw new ArgumentException("Tokenizer asset paths must be unique.", nameof(Assets));
        foreach (var asset in Assets) asset.Validate();
        if (SpecialTokens is null || SpecialTokens.Any(string.IsNullOrWhiteSpace) || SpecialTokens.Distinct(StringComparer.Ordinal).Count() != SpecialTokens.Count)
            throw new ArgumentException("Special tokens must be nonempty and unique.", nameof(SpecialTokens));
        if (Family == TokenizerAssetFamily.Fixture)
        {
            if (Adapter != TokenizerAdapterId.Fixture || !FixtureOnly) throw new ArgumentException("Fixture assets require the fixture adapter and fixtureOnly=true.");
        }
        else if (Family == TokenizerAssetFamily.Tiktoken)
        {
            if (Adapter != TokenizerAdapterId.Tiktoken || FixtureOnly || !Revision.StartsWith("sha256:", StringComparison.Ordinal) || Revision.Length != 71 ||
                !Revision.AsSpan(7).ToString().All(Uri.IsHexDigit)) throw new ArgumentException("Tiktoken assets require a pinned SHA-256 revision and the production tiktoken adapter.");
        }
        else throw new ArgumentException("Unknown tokenizer family.", nameof(Family));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Tokenizer manifest fields must be nonempty.", name);
    }
}

public sealed record TokenizerAsset(string Path, string Sha256)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || Path.Contains('\\') || Path.StartsWith('/') || Path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Tokenizer asset path must be a normalized relative path.", nameof(Path));
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("Tokenizer asset SHA-256 must contain 64 hexadecimal characters.", nameof(Sha256));
    }
}

public sealed class TokenizerAssetFamilyJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadKnown(ref reader, [TokenizerAssetFamily.Fixture, TokenizerAssetFamily.Tiktoken]);
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => WriteKnown(writer, value, [TokenizerAssetFamily.Fixture, TokenizerAssetFamily.Tiktoken]);
    internal static string ReadKnown(ref Utf8JsonReader reader, string[] values) { var s = reader.GetString(); return s is not null && values.Contains(s, StringComparer.Ordinal) ? s : throw new JsonException("Unknown tokenizer identity."); }
    internal static void WriteKnown(Utf8JsonWriter writer, string value, string[] values) { if (!values.Contains(value, StringComparer.Ordinal)) throw new JsonException("Unknown tokenizer identity."); writer.WriteStringValue(value); }
}

public sealed class TokenizerAdapterIdJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => TokenizerAssetFamilyJsonConverter.ReadKnown(ref reader, [TokenizerAdapterId.Fixture, TokenizerAdapterId.Tiktoken]);
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => TokenizerAssetFamilyJsonConverter.WriteKnown(writer, value, [TokenizerAdapterId.Fixture, TokenizerAdapterId.Tiktoken]);
}
