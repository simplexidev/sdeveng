using System.Security.Cryptography;
using System.Text;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Measures explicit request variants through a verified registered tokenizer.</summary>
public sealed class RequestVariantTokenMeasurer(TokenizerRegistry tokenizers) : IRequestVariantTokenMeasurer
{
    public RequestVariantTokenMeasurement Measure(string tokenizerId, RequestVariantTokenInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.RequiredFactMappings);
        ArgumentNullException.ThrowIfNull(input.RequiredFacts);
        ArgumentNullException.ThrowIfNull(input.OriginalText);
        ArgumentNullException.ThrowIfNull(input.NormalizedText);
        ArgumentNullException.ThrowIfNull(input.CondensedText);
        if (input.RequiredFacts.Any(string.IsNullOrWhiteSpace) || input.RequiredFacts.Distinct(StringComparer.Ordinal).Count() != input.RequiredFacts.Count ||
            input.RequiredFacts.Count != input.RequiredFactMappings.Count || input.RequiredFacts.Any(fact => !input.RequiredFactMappings.ContainsKey(fact)) ||
            input.RequiredFactMappings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                !input.CondensedText.Contains(pair.Value, StringComparison.Ordinal)))
            throw new ArgumentException("Every required fact must have a mapping to nonempty text retained in the condensed request.", nameof(input));

        var tokenizer = tokenizers.Resolve(tokenizerId);
        var result = new RequestVariantTokenMeasurement(RequestVariantTokenMeasurement.CurrentSchemaVersion,
            RequestVariantTokenMeasurement.ResultKind, "exact",
            tokenizer.Manifest.Adapter == TokenizerAdapterId.Tiktoken ? "tiktoken-v1" : "fixture-byte-v1",
            tokenizer.Manifest.Id, tokenizer.Manifest.Revision, tokenizer.Manifest.Assets, tokenizer.Manifest.FixtureOnly,
            MeasureText(input.OriginalText, tokenizer), MeasureText(input.NormalizedText, tokenizer),
            MeasureText(input.CondensedText, tokenizer), 0, null, null, new Dictionary<string, string>(input.RequiredFactMappings, StringComparer.Ordinal));
        result = result with
        {
            RequestTokensSaved = result.Original.Tokens - result.Condensed.Tokens,
            CompressionRatio = result.Original.Tokens == 0 ? null : (decimal)result.Condensed.Tokens / result.Original.Tokens,
            CompressionRatioUnavailableReason = result.Original.Tokens == 0 ? "original-token-count-zero" : null
        };
        result.Validate();
        return result;
    }

    private static RequestTextTokenMeasurement MeasureText(string text, ITokenizerAdapter tokenizer)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, tokenizer.CountTokens(text));
    }
}
