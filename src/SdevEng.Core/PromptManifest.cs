using System.Text.Json.Serialization;
using System.Text.Json;

namespace SdevEng;

/// <summary>Stable identities for the ordered parts of a rendered worker input.</summary>
public enum PromptComponentId
{
    System,
    Role,
    SkillMetadata,
    SkillInstructions,
    SkillReferences,
    Request,
    Evidence,
    State,
    Tools,
    OutputContract
}

/// <summary>A prompt component's identity and attribution, separate from its referenced content.</summary>
public sealed record PromptComponent(
    [property: JsonConverter(typeof(PromptComponentIdJsonConverter))]
    PromptComponentId Id,
    string Role,
    string Provenance,
    string ContentReference,
    string ContentHash);

public sealed class PromptComponentIdJsonConverter : JsonConverter<PromptComponentId>
{
    public override PromptComponentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        foreach (var id in Enum.GetValues<PromptComponentId>())
            if (string.Equals(value, ToWireValue(id), StringComparison.Ordinal)) return id;
        throw new JsonException("Unknown prompt component identity.");
    }

    public override void Write(Utf8JsonWriter writer, PromptComponentId value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown prompt component identity.");
        writer.WriteStringValue(ToWireValue(value));
    }

    private static string ToWireValue(PromptComponentId value) => value switch
    {
        PromptComponentId.SkillMetadata => "skill-metadata",
        PromptComponentId.SkillInstructions => "skill-instructions",
        PromptComponentId.SkillReferences => "skill-references",
        PromptComponentId.OutputContract => "output-contract",
        _ => value.ToString().ToLowerInvariant()
    };
}

/// <summary>Versioned, ordered manifest of components used to construct a worker input.</summary>
public sealed record PromptManifest(int SchemaVersion, IReadOnlyList<PromptComponent> Components)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly PromptComponentId[] CanonicalOrder =
    [
        PromptComponentId.System,
        PromptComponentId.Role,
        PromptComponentId.SkillMetadata,
        PromptComponentId.SkillInstructions,
        PromptComponentId.SkillReferences,
        PromptComponentId.Request,
        PromptComponentId.Evidence,
        PromptComponentId.State,
        PromptComponentId.Tools,
        PromptComponentId.OutputContract
    ];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "Unsupported prompt manifest schema version.");
        if (Components is null) throw new ArgumentException("Prompt components are required.", nameof(Components));

        var seen = new HashSet<PromptComponentId>();
        var priorIndex = -1;
        foreach (var component in Components)
        {
            if (component is null) throw new ArgumentException("Prompt components cannot contain null entries.", nameof(Components));
            if (!Enum.IsDefined(component.Id)) throw new ArgumentException("Unknown prompt component identity.", nameof(Components));
            if (!seen.Add(component.Id)) throw new ArgumentException($"Duplicate prompt component identity: {component.Id}.", nameof(Components));
            var index = Array.IndexOf(CanonicalOrder, component.Id);
            if (index < priorIndex) throw new ArgumentException("Prompt components must follow canonical order.", nameof(Components));
            priorIndex = index;
            Require(component.Role, nameof(component.Role));
            Require(component.Provenance, nameof(component.Provenance));
            Require(component.ContentReference, nameof(component.ContentReference));
            Require(component.ContentHash, nameof(component.ContentHash));
        }

        foreach (var required in new[] { PromptComponentId.System, PromptComponentId.Role, PromptComponentId.Request, PromptComponentId.OutputContract })
            if (!seen.Contains(required)) throw new ArgumentException($"Required prompt component is missing: {required}.", nameof(Components));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Prompt component fields must be nonempty.", name);
    }
}
