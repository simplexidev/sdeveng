using System.Text;
using System.Text.Json;
using SdevEng;

namespace SdevEng.Infrastructure;

public sealed record ChatTemplateRenderResult(bool Available, string? Text, string? Reason)
{
    public IReadOnlyList<RenderedComponentSpan>? Spans { get; init; }
}

/// <summary>Validates template metadata, binds the exact registered tokenizer, and renders deterministic prompt bytes.</summary>
public sealed class ChatTemplateRegistry(TokenizerRegistry tokenizers)
{
    private readonly Dictionary<(string Id, string Revision, string Checksum), ChatTemplateManifest> _templates = new();

    internal ChatTemplateManifest? Find(string id, string revision, string checksum) =>
        _templates.GetValueOrDefault((id, revision, checksum));

    internal ITokenizerAdapter ResolveTokenizer(ChatTemplateManifest template) => tokenizers.Resolve(template.TokenizerId);

    internal TokenizerManifest TokenizerMetadata(ChatTemplateManifest template) => tokenizers.Get(template.TokenizerId);

    public ChatTemplateManifest RegisterFile(string path)
    {
        var manifest = ReadMetadata(path);
        Register(manifest);
        return manifest;
    }

    public static ChatTemplateManifest ReadMetadata(string path)
    {
        var manifest = JsonSerializer.Deserialize<ChatTemplateManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Chat-template manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public void Register(ChatTemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (_templates.ContainsKey((manifest.Id, manifest.Revision, manifest.Checksum))) throw new ArgumentException("Chat-template identity is already registered.", nameof(manifest));
        var tokenizer = tokenizers.Get(manifest.TokenizerId);
        _ = tokenizers.Resolve(manifest.TokenizerId);
        if (!StringComparer.Ordinal.Equals(tokenizer.Revision, manifest.TokenizerRevision)) throw new ArgumentException("Chat template references a different tokenizer revision.", nameof(manifest));
        _templates.Add((manifest.Id, manifest.Revision, manifest.Checksum), manifest);
    }

    public ChatTemplateRenderResult Render(string id, string revision, string checksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference,
        bool includeSpans = false)
    {
        if (!_templates.TryGetValue((id, revision, checksum), out var template)) return new(false, null, "chat-template-unavailable");
        try
        {
            prompt.Validate();
            var tokenizer = tokenizers.Get(template.TokenizerId);
            _ = tokenizers.Resolve(template.TokenizerId);
            if (!StringComparer.Ordinal.Equals(tokenizer.Revision, template.TokenizerRevision)) return new(false, null, "tokenizer-unavailable");
            var output = new StringBuilder();
            var spans = new List<RenderedComponentSpan>();
            void Append(string text, string componentId, string reference)
            {
                if (includeSpans) spans.Add(new(componentId, reference, output.Length, text.Length));
                output.Append(text);
            }
            void Overhead(string text, string reference) => Append(text, RenderedComponentSpan.Overhead, reference);
            var first = true;
            foreach (var component in prompt.Components)
            {
                if (component.Id == PromptComponentId.Tools)
                {
                    var tools = component.Tools;
                    if (tools is not { Count: > 0 })
                    {
                        Append("", "tools", component.ContentReference);
                        continue;
                    }
                    if (!first) Overhead(template.MessageSeparator, "message-separator");
                    Overhead(template.MessageStart + "tools" + template.HeaderBodySeparator + template.ToolSectionStart, "tool-header");
                    using var stream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        writer.WriteStartArray();
                        foreach (var tool in tools.OrderBy(t => t.Id, StringComparer.Ordinal))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("description", tool.Description); writer.WriteString("id", tool.Id); writer.WriteString("name", tool.Name);
                            writer.WriteString("schemaReference", tool.SchemaReference); writer.WriteString("type", tool.Type);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    Append(Encoding.UTF8.GetString(stream.ToArray()), "tools", component.ContentReference);
                    Overhead(template.ToolSectionEnd + template.MessageEnd, "tool-footer");
                    first = false;
                    continue;
                }
                if ((!component.IsLoaded && component.Id != PromptComponentId.SkillReferences) ||
                    !textByContentReference.TryGetValue(component.ContentReference, out var body)) return new(false, null, "prompt-content-unavailable");
                if (component.Id == PromptComponentId.SkillReferences && component.IsLoaded &&
                    component.ContentHash != "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant())
                    return new(false, null, "reference-hash-mismatch");
                if (!first) Overhead(template.MessageSeparator, "message-separator");
                Overhead(template.MessageStart + component.Role + template.HeaderBodySeparator, "message-header");
                Append(body, PromptComponentIdJsonConverter.ToWireValue(component.Id), component.ContentReference);
                Overhead(template.MessageEnd, "message-footer");
                first = false;
            }
            Overhead(template.GenerationPrefix, "generation-prefix");
            return new(true, output.ToString(), null) { Spans = includeSpans ? spans.ToArray() : null };
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, null, "chat-template-or-tokenizer-unavailable");
        }
    }
}
