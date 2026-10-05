using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace SdevEng;

public static class ChatTemplateFamily
{
    public const string Fixture = "fixture";
    public const string Canonical = "canonical-v1";
}

/// <summary>Data-only message wrapping metadata. It is never interpreted as executable template code.</summary>
public sealed record ChatTemplateManifest(
    int SchemaVersion, string Id, string Revision, string Checksum,
    string TokenizerId, string TokenizerRevision,
    [property: JsonConverter(typeof(ChatTemplateFamilyJsonConverter))] string Family,
    string MessageStart, string HeaderBodySeparator, string MessageEnd,
    string MessageSeparator, string ToolSectionStart, string ToolSectionEnd,
    string GenerationPrefix, bool FixtureOnly = false)
{
    public const int CurrentSchemaVersion = 1;

    public string ComputeChecksum()
    {
        var canonical = System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion, Id, Revision, TokenizerId, TokenizerRevision, Family, MessageStart, HeaderBodySeparator, MessageEnd, MessageSeparator, ToolSectionStart, ToolSectionEnd, GenerationPrefix, FixtureOnly });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentException("Unsupported chat-template schema version.");
        foreach (var value in new[] { Id, Revision, TokenizerId, TokenizerRevision, MessageStart, HeaderBodySeparator, MessageEnd, MessageSeparator, ToolSectionStart, ToolSectionEnd, GenerationPrefix })
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Chat-template fields must be nonempty.");
        if (Checksum.Length != 64 || !Checksum.All(Uri.IsHexDigit)) throw new ArgumentException("Chat-template checksum must be a SHA-256 digest.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Checksum, ComputeChecksum())) throw new ArgumentException("Chat-template checksum does not match its canonical metadata.");
        if (Family == ChatTemplateFamily.Fixture ? !FixtureOnly : Family != ChatTemplateFamily.Canonical || FixtureOnly)
            throw new ArgumentException("Unknown or inconsistent chat-template family.");
    }
}

public sealed class ChatTemplateFamilyJsonConverter : JsonConverter<string>
{
    public override string Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value is ChatTemplateFamily.Fixture or ChatTemplateFamily.Canonical ? value : throw new System.Text.Json.JsonException("Unknown chat-template identity.");
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is not (ChatTemplateFamily.Fixture or ChatTemplateFamily.Canonical)) throw new System.Text.Json.JsonException("Unknown chat-template identity.");
        writer.WriteStringValue(value);
    }
}
