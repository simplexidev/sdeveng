namespace SdevEng;

public sealed record RenderedInputTokenPolicy(bool RequireExact = true, bool AllowEstimate = false);

public interface IRenderedInputTokenCounter
{
    RenderedInputTokenMeasurement Count(string templateId, string templateRevision, string templateChecksum,
        PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference, RenderedInputTokenPolicy? policy = null);

    RenderedInputTokenMeasurement CountAttributed(string templateId, string templateRevision, string templateChecksum,
        PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference);
}

/// <summary>Serializable attribution for one full rendered input, never a sum of component counts.</summary>
public sealed record RenderedInputTokenMeasurement(
    int SchemaVersion, string Kind, string MeasurementKind, long? Tokens, string? Method,
    string TemplateId, string TemplateRevision, string TemplateChecksum,
    string? TokenizerId, string? TokenizerRevision, IReadOnlyList<TokenizerAsset> TokenizerAssets,
    bool FixtureOnly, string? RenderedInputDigest, long? Utf8Bytes, string? UnavailableReason)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OrderedComponentAttribution? Attribution { get; init; }

    public const int CurrentSchemaVersion = 1;
    public const string ResultKind = "rendered-input-token-measurement";

    public void Validate()
    {
        static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (SchemaVersion != CurrentSchemaVersion || Kind != ResultKind ||
            string.IsNullOrWhiteSpace(TemplateId) || string.IsNullOrWhiteSpace(TemplateRevision) || !Digest(TemplateChecksum) ||
            TokenizerAssets is null || TokenizerAssets.Any(a => a is null)) throw new ArgumentException("Invalid rendered-input measurement attribution.");
        foreach (var asset in TokenizerAssets) asset.Validate();
        if ((TokenizerId is null) != (TokenizerRevision is null) ||
            (TokenizerId is null ? TokenizerAssets.Count != 0 : string.IsNullOrWhiteSpace(TokenizerId) || string.IsNullOrWhiteSpace(TokenizerRevision) || TokenizerAssets.Count == 0) ||
            (RenderedInputDigest is null) != (Utf8Bytes is null) || Utf8Bytes < 0 ||
            RenderedInputDigest is not null && !Digest(RenderedInputDigest)) throw new ArgumentException("Invalid rendered-input measurement provenance.");
        if (MeasurementKind == "unavailable")
        {
            if (Tokens is not null || Method is not null || string.IsNullOrWhiteSpace(UnavailableReason)) throw new ArgumentException("Invalid unavailable measurement.");
        }
        else if (MeasurementKind is "exact" or "estimated")
        {
            if (Tokens is null or < 0 || RenderedInputDigest is null || TokenizerId is null || UnavailableReason is not null ||
                (MeasurementKind == "exact" ? !((Method == "fixture-byte-v1" && FixtureOnly) || (Method == "tiktoken-v1" && !FixtureOnly))
                    : Method != "ceil-utf8-bytes-div-4" || Tokens != (Utf8Bytes + 3) / 4))
                throw new ArgumentException("Invalid token measurement.");
        }
        else throw new ArgumentException("Unknown token measurement kind.");
        if (Attribution is not null)
        {
            if (MeasurementKind != "exact") throw new ArgumentException("Attribution requires exact counting.");
            Attribution.Validate();
            if (Attribution.TotalTokens != Tokens) throw new ArgumentException("Attribution does not reconcile to the authoritative total.");
        }
    }
}
