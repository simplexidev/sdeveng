using System.Security.Cryptography;
using System.Text;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Resolves verified assets and tokenizes the final chat rendering once, without inference.</summary>
public sealed class RenderedInputTokenCounter(ChatTemplateRegistry templates) : IRenderedInputTokenCounter
{
    public RenderedInputTokenMeasurement Count(string templateId, string templateRevision, string templateChecksum,
        PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference, RenderedInputTokenPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(textByContentReference);
        policy ??= new();
        var template = templates.Find(templateId, templateRevision, templateChecksum);
        var result = new RenderedInputTokenMeasurement(1, RenderedInputTokenMeasurement.ResultKind, "unavailable", null, null,
            templateId, templateRevision, templateChecksum, null, null, [], template?.FixtureOnly ?? false, null, null, "chat-template-unavailable");
        if (template is null) return Checked(result);
        var metadata = templates.TokenizerMetadata(template);
        result = result with
        {
            TokenizerId = metadata.Id,
            TokenizerRevision = metadata.Revision,
            TokenizerAssets = metadata.Assets,
            FixtureOnly = template.FixtureOnly || metadata.FixtureOnly
        };
        ITokenizerAdapter adapter;
        try { adapter = templates.ResolveTokenizer(template); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
        {
            return Checked(result with { UnavailableReason = "tokenizer-unavailable" });
        }
        result = result with
        {
            TokenizerId = adapter.Manifest.Id,
            TokenizerRevision = adapter.Manifest.Revision,
            TokenizerAssets = adapter.Manifest.Assets,
            FixtureOnly = template.FixtureOnly || adapter.Manifest.FixtureOnly
        };
        var rendering = templates.Render(templateId, templateRevision, templateChecksum, prompt, textByContentReference);
        if (!rendering.Available) return Checked(result with { UnavailableReason = rendering.Reason });
        var bytes = Encoding.UTF8.GetBytes(rendering.Text!);
        result = result with { RenderedInputDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Utf8Bytes = bytes.LongLength };
        try
        {
            var count = adapter.CountTokens(rendering.Text!);
            return Checked(result with
            {
                MeasurementKind = "exact",
                Tokens = count,
                Method = adapter.Manifest.Adapter == TokenizerAdapterId.Tiktoken ? "tiktoken-v1" : "fixture-byte-v1",
                UnavailableReason = null
            });
        }
        catch (NotSupportedException)
        {
            if (policy.RequireExact || !policy.AllowEstimate) return Checked(result with { UnavailableReason = "exact-tokenization-unavailable" });
            return Checked(result with
            {
                MeasurementKind = "estimated",
                Tokens = (bytes.LongLength + 3) / 4,
                Method = "ceil-utf8-bytes-div-4",
                UnavailableReason = null
            });
        }
    }

    private static RenderedInputTokenMeasurement Checked(RenderedInputTokenMeasurement result) { result.Validate(); return result; }
}
