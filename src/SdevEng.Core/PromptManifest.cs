using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;

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

public enum PromptRequestContentKind
{
    Text,
    FactMapping
}

public enum PromptRequestProjection
{
    Original,
    Normalized,
    Condensed
}

public static class PromptEvidenceReferenceKind
{
    public const string EvidencePackItem = "evidence-pack-item";
}

public static class PromptStateContentKind
{
    public const string RunFacts = "run-facts";
    public const string WorkUnitFacts = "work-unit-facts";
    public const string SyntheticPlaceholder = "synthetic-placeholder";
}

/// <summary>A tool exposed to a worker through the prompt manifest.</summary>
public sealed record PromptToolDescriptor(string Id, string Name, string Description, string SchemaReference, string Type = "function");

/// <summary>A prompt component's identity and attribution, separate from its referenced content.</summary>
public sealed record PromptComponent(
    [property: JsonConverter(typeof(PromptComponentIdJsonConverter))]
    PromptComponentId Id,
    string Role,
    string Provenance,
    string ContentReference,
    string ContentHash,
    bool IsLoaded = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(PromptRequestContentKindJsonConverter))]
    PromptRequestContentKind? RequestContentKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(PromptRequestProjectionJsonConverter))]
    PromptRequestProjection? RequestProjection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? OriginalArtifactReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(PromptEvidenceReferenceKindJsonConverter))]
    string? EvidenceReferenceKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EvidenceItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(PromptStateContentKindJsonConverter))]
    string? StateContentKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PromptToolDescriptor>? Tools = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResponseSchema = null);

public sealed class PromptEvidenceReferenceKindJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value != PromptEvidenceReferenceKind.EvidencePackItem) throw new JsonException("Unknown evidence reference kind.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value != PromptEvidenceReferenceKind.EvidencePackItem) throw new JsonException("Unknown evidence reference kind.");
        writer.WriteStringValue(value);
    }
}

public sealed class PromptStateContentKindJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is not (PromptStateContentKind.RunFacts or PromptStateContentKind.WorkUnitFacts or PromptStateContentKind.SyntheticPlaceholder))
            throw new JsonException("Unknown state content kind.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (value is not (PromptStateContentKind.RunFacts or PromptStateContentKind.WorkUnitFacts or PromptStateContentKind.SyntheticPlaceholder))
            throw new JsonException("Unknown state content kind.");
        writer.WriteStringValue(value);
    }
}

public sealed class PromptRequestContentKindJsonConverter : JsonConverter<PromptRequestContentKind?>
{
    public override PromptRequestContentKind? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        null => null,
        "text" => PromptRequestContentKind.Text,
        "fact-mapping" => PromptRequestContentKind.FactMapping,
        _ => throw new JsonException("Unknown request content kind.")
    };

    public override void Write(Utf8JsonWriter writer, PromptRequestContentKind? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.Value switch
        {
            PromptRequestContentKind.Text => "text",
            PromptRequestContentKind.FactMapping => "fact-mapping",
            _ => throw new JsonException("Unknown request content kind.")
        });
    }
}

public sealed class PromptRequestProjectionJsonConverter : JsonConverter<PromptRequestProjection?>
{
    public override PromptRequestProjection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        null => null,
        "original" => PromptRequestProjection.Original,
        "normalized" => PromptRequestProjection.Normalized,
        "condensed" => PromptRequestProjection.Condensed,
        _ => throw new JsonException("Unknown request projection.")
    };

    public override void Write(Utf8JsonWriter writer, PromptRequestProjection? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.Value switch
        {
            PromptRequestProjection.Original => "original",
            PromptRequestProjection.Normalized => "normalized",
            PromptRequestProjection.Condensed => "condensed",
            _ => throw new JsonException("Unknown request projection.")
        });
    }
}

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

    public static string ToWireValue(PromptComponentId value) => value switch
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
    public const int CurrentSchemaVersion = 2;
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
        if (SchemaVersion is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "Unsupported prompt manifest schema version.");
        if (Components is null) throw new ArgumentException("Prompt components are required.", nameof(Components));

        var seen = new HashSet<PromptComponentId>();
        var seenSkillIdentities = new HashSet<(PromptComponentId Id, string Reference)>();
        var priorIndex = -1;
        string? priorSkillReference = null;
        foreach (var component in Components)
        {
            if (component is null) throw new ArgumentException("Prompt components cannot contain null entries.", nameof(Components));
            if (!Enum.IsDefined(component.Id)) throw new ArgumentException("Unknown prompt component identity.", nameof(Components));
            var isSkillComponent = component.Id is PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions or PromptComponentId.SkillReferences;
            if (!isSkillComponent && !seen.Add(component.Id)) throw new ArgumentException($"Duplicate prompt component identity: {component.Id}.", nameof(Components));
            if (isSkillComponent && !seenSkillIdentities.Add((component.Id, component.ContentReference)))
                throw new ArgumentException($"Duplicate skill component identity: {component.ContentReference}.", nameof(Components));
            var index = Array.IndexOf(CanonicalOrder, component.Id);
            if (index < priorIndex) throw new ArgumentException("Prompt components must follow canonical order.", nameof(Components));
            if (isSkillComponent && index != priorIndex) priorSkillReference = null;
            if (isSkillComponent && priorSkillReference is not null && string.CompareOrdinal(component.ContentReference, priorSkillReference) < 0)
                throw new ArgumentException("Skill components must follow stable content-reference order.", nameof(Components));
            priorIndex = index;
            Require(component.Role, nameof(component.Role));
            Require(component.Provenance, nameof(component.Provenance));
            Require(component.ContentReference, nameof(component.ContentReference));
            Require(component.ContentHash, nameof(component.ContentHash));
            ValidateRequestMetadata(component);
            ValidateEvidenceAndStateMetadata(component);
            ValidateToolAndOutputMetadata(component);
            if (component.Id == PromptComponentId.SkillReferences && component.IsLoaded && SchemaVersion == 1)
                throw new ArgumentException("Skill resource references cannot be marked as loaded content.", nameof(Components));
            if (component.Id == PromptComponentId.SkillReferences && component.IsLoaded &&
                (!System.Text.RegularExpressions.Regex.IsMatch(component.ContentHash, "^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                 component.ContentReference.Split('/').Length < 3))
                throw new ArgumentException("Loaded references require a canonical skill/version/resource and SHA256 hash.", nameof(Components));
            if (isSkillComponent && !component.ContentReference.Contains('/', StringComparison.Ordinal))
                throw new ArgumentException("Skill component references must identify a canonical skill/version/resource.", nameof(Components));
            if (isSkillComponent) priorSkillReference = component.ContentReference;
            else priorSkillReference = null;
        }

        foreach (var required in new[] { PromptComponentId.System, PromptComponentId.Role, PromptComponentId.Request, PromptComponentId.OutputContract })
            if (required is PromptComponentId.System or PromptComponentId.Role or PromptComponentId.Request or PromptComponentId.OutputContract && !seen.Contains(required))
                throw new ArgumentException($"Required prompt component is missing: {required}.", nameof(Components));
    }

    private static void ValidateToolAndOutputMetadata(PromptComponent component)
    {
        if (component.Id == PromptComponentId.Tools)
        {
            if (component.ResponseSchema is not null)
                throw new ArgumentException("Response schema is only valid on the output-contract component.", nameof(Components));
            if (component.Tools is not null)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tool in component.Tools)
                {
                    if (tool is null || string.IsNullOrWhiteSpace(tool.Id) || string.IsNullOrWhiteSpace(tool.Name) ||
                        string.IsNullOrWhiteSpace(tool.Description) || string.IsNullOrWhiteSpace(tool.SchemaReference) || tool.Type != "function")
                        throw new ArgumentException("Tool descriptors require id, name, description, and schema reference.", nameof(Components));
                    if (!ids.Add(tool.Id)) throw new ArgumentException("Tool descriptor IDs must be unique.", nameof(Components));
                }
            }
            return;
        }

        if (component.Tools is not null)
            throw new ArgumentException("Tool descriptors are only valid on the tools component.", nameof(Components));
        if (component.Id == PromptComponentId.OutputContract)
        {
            if (string.IsNullOrWhiteSpace(component.ResponseSchema))
                throw new ArgumentException("Response schema identity must be nonempty.", nameof(Components));
        }
        else if (component.ResponseSchema is not null)
            throw new ArgumentException("Response schema is only valid on the output-contract component.", nameof(Components));
    }

    private static void ValidateRequestMetadata(PromptComponent component)
    {
        if (component.Id != PromptComponentId.Request)
        {
            if (component.RequestContentKind.HasValue || component.RequestProjection.HasValue || component.OriginalArtifactReference is not null)
                throw new ArgumentException("Request metadata is only valid on the request component.", nameof(Components));
            return;
        }

        if (component.RequestContentKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentException("Unknown request content kind.", nameof(Components));
        if (component.RequestProjection is { } projection && !Enum.IsDefined(projection))
            throw new ArgumentException("Unknown request projection.", nameof(Components));
        var hasMetadata = component.RequestContentKind.HasValue || component.RequestProjection.HasValue || component.OriginalArtifactReference is not null;
        if (hasMetadata && (!component.RequestContentKind.HasValue || !component.RequestProjection.HasValue || string.IsNullOrWhiteSpace(component.OriginalArtifactReference)))
            throw new ArgumentException("Request content kind, projection, and original artifact reference must be supplied together.", nameof(Components));
    }

    private static void ValidateEvidenceAndStateMetadata(PromptComponent component)
    {
        if (component.Id == PromptComponentId.Evidence)
        {
            if (component.EvidenceReferenceKind is { } kind && kind != PromptEvidenceReferenceKind.EvidencePackItem)
                throw new ArgumentException("Unknown evidence reference kind.", nameof(Components));
            if (component.EvidenceReferenceKind is not null != !string.IsNullOrWhiteSpace(component.EvidenceItemId))
                throw new ArgumentException("Evidence components must identify an EvidencePack item.", nameof(Components));
            if (component.EvidenceItemId is { } evidenceId && !System.Text.RegularExpressions.Regex.IsMatch(evidenceId, "^evidence:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new ArgumentException("Evidence item identity must be a canonical EvidencePack ID.", nameof(Components));
            if (component.StateContentKind is not null)
                throw new ArgumentException("State metadata is only valid on the state component.", nameof(Components));
            return;
        }
        if (component.EvidenceReferenceKind is not null || component.EvidenceItemId is not null)
            throw new ArgumentException("Evidence metadata is only valid on the evidence component.", nameof(Components));
        if (component.Id == PromptComponentId.State)
        {
            if (component.StateContentKind is { } kind && kind is not (PromptStateContentKind.RunFacts or PromptStateContentKind.WorkUnitFacts or PromptStateContentKind.SyntheticPlaceholder))
                throw new ArgumentException("Unknown state content kind.", nameof(Components));
        }
        else if (component.StateContentKind is not null)
            throw new ArgumentException("State metadata is only valid on the state component.", nameof(Components));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Prompt component fields must be nonempty.", name);
    }
}

/// <summary>A deterministic measurement of a manifest text projection; this is not model-rendered input.</summary>
public sealed record PromptManifestSizeMeasurement(int SchemaVersion, string Kind, string MeasurementKind, string RendererRevision,
    IReadOnlyList<PromptComponentSizeMeasurement> Components, PromptTextSizeMeasurement? CompletePrompt)
{
    public const int CurrentSchemaVersion = 1;
    public const string ProjectionKind = "manifest-text-projection";
    public const string ProjectionRevision = "manifest-text-projection/v1";

    public static PromptManifestSizeMeasurement Measure(PromptManifest manifest, IReadOnlyDictionary<string, string> textByContentReference)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(textByContentReference);
        manifest.Validate();
        var components = manifest.Components.Select(component =>
        {
            if (!component.IsLoaded || !textByContentReference.TryGetValue(component.ContentReference, out var text))
                return new PromptComponentSizeMeasurement(component.Id, component.ContentReference, component.Provenance, null, null, null, "component-text-unavailable");
            var bytes = (long)Encoding.UTF8.GetByteCount(text);
            var characters = (long)text.EnumerateRunes().Count();
            var normalizedLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var lines = text.Length == 0 ? 0L : normalizedLines.Count(c => c == '\n') + 1L;
            return new PromptComponentSizeMeasurement(component.Id, component.ContentReference, component.Provenance, bytes, characters, lines, null);
        }).ToArray();
        var completeText = new StringBuilder();
        var completeAvailable = true;
        foreach (var component in manifest.Components)
        {
            if (!component.IsLoaded || !textByContentReference.TryGetValue(component.ContentReference, out var text))
            {
                completeAvailable = false;
                break;
            }
            completeText.Append(text);
        }
        var completePrompt = completeAvailable
            ? MeasureText(completeText.ToString()) with { MeasurementKind = "complete-manifest-text-projection" }
            : new PromptTextSizeMeasurement("complete-manifest-text-projection", null, null, null, "component-text-unavailable");
        return new(CurrentSchemaVersion, "prompt-manifest-size-measurement", ProjectionKind, ProjectionRevision, components, completePrompt);
    }

    private static PromptTextSizeMeasurement MeasureText(string text)
    {
        var normalizedLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return new("component-text", Encoding.UTF8.GetByteCount(text), text.EnumerateRunes().LongCount(),
            text.Length == 0 ? 0 : normalizedLines.Count(c => c == '\n') + 1, null);
    }
}

public sealed record PromptComponentSizeMeasurement([property: JsonConverter(typeof(PromptComponentIdJsonConverter))] PromptComponentId ComponentId, string ContentReference, string Provenance,
    long? Utf8Bytes, long? UnicodeScalarValues, long? Lines, string? UnavailableReason);

public sealed record PromptTextSizeMeasurement(string MeasurementKind, long? Utf8Bytes, long? UnicodeScalarValues, long? Lines, string? UnavailableReason);
