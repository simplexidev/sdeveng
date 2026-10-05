using System.Text;
using System.Text.Json;
using SdevEng;

namespace SdevEng.Infrastructure;

public sealed record ChatTemplateRenderResult(bool Available, string? Text, string? Reason);

/// <summary>Validates template metadata, binds the exact registered tokenizer, and renders deterministic prompt bytes.</summary>
public sealed class ChatTemplateRegistry(TokenizerRegistry tokenizers)
{
    private readonly Dictionary<(string Id, string Revision, string Checksum), ChatTemplateManifest> _templates = new();

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

    public ChatTemplateRenderResult Render(string id, string revision, string checksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference)
    {
        if (!_templates.TryGetValue((id, revision, checksum), out var template)) return new(false, null, "chat-template-unavailable");
        try
        {
            prompt.Validate();
            var tokenizer = tokenizers.Get(template.TokenizerId);
            _ = tokenizers.Resolve(template.TokenizerId);
            if (!StringComparer.Ordinal.Equals(tokenizer.Revision, template.TokenizerRevision)) return new(false, null, "tokenizer-unavailable");
            var output = new StringBuilder();
            var first = true;
            foreach (var component in prompt.Components)
            {
                if (component.Id == PromptComponentId.Tools)
                {
                    var tools = component.Tools;
                    if (tools is not { Count: > 0 }) continue;
                    if (!first) output.Append(template.MessageSeparator);
                    output.Append(template.MessageStart).Append("tools").Append(template.HeaderBodySeparator).Append(template.ToolSectionStart);
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
                    output.Append(Encoding.UTF8.GetString(stream.ToArray())).Append(template.ToolSectionEnd).Append(template.MessageEnd);
                    first = false;
                    continue;
                }
                if (!component.IsLoaded || !textByContentReference.TryGetValue(component.ContentReference, out var body)) return new(false, null, "prompt-content-unavailable");
                if (!first) output.Append(template.MessageSeparator);
                output.Append(template.MessageStart).Append(component.Role).Append(template.HeaderBodySeparator).Append(body).Append(template.MessageEnd);
                first = false;
            }
            output.Append(template.GenerationPrefix);
            return new(true, output.ToString(), null);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, null, "chat-template-or-tokenizer-unavailable");
        }
    }
}
