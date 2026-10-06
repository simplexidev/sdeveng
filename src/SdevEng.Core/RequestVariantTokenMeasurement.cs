namespace SdevEng;

/// <summary>Explicit request text variants and caller supplied evidence that normalization retained required facts.</summary>
public sealed record RequestVariantTokenInput(string OriginalText, string NormalizedText,
    IReadOnlyDictionary<string, string> RequiredFactMappings);

/// <summary>Versioned, attributable token counts for supplied request variants.</summary>
public sealed record RequestVariantTokenMeasurement(int SchemaVersion, string Kind, string MeasurementKind,
    string Method, string TokenizerId, string TokenizerRevision, IReadOnlyList<TokenizerAsset> TokenizerAssets,
    bool FixtureOnly, RequestTextTokenMeasurement Original, RequestTextTokenMeasurement Normalized)
{
    public const int CurrentSchemaVersion = 1;
    public const string ResultKind = "request-variant-token-measurement";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Kind != ResultKind || MeasurementKind != "exact" ||
            string.IsNullOrWhiteSpace(Method) || string.IsNullOrWhiteSpace(TokenizerId) || string.IsNullOrWhiteSpace(TokenizerRevision) ||
            TokenizerAssets is null || TokenizerAssets.Count == 0 || Original is null || Normalized is null)
            throw new ArgumentException("Invalid request variant measurement.");
        foreach (var asset in TokenizerAssets) asset.Validate();
        Original.Validate();
        Normalized.Validate();
    }
}

public sealed record RequestTextTokenMeasurement(string TextDigest, long Utf8Bytes, long Tokens)
{
    public void Validate()
    {
        if (TextDigest is not { Length: 64 } || !TextDigest.All(Uri.IsHexDigit) || Utf8Bytes < 0 || Tokens < 0)
            throw new ArgumentException("Invalid request text token measurement.");
    }
}

public interface IRequestVariantTokenMeasurer
{
    RequestVariantTokenMeasurement Measure(string tokenizerId, RequestVariantTokenInput input);
}
