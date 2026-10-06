using System.Collections.ObjectModel;
using System.Text;
using Microsoft.ML.Tokenizers;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Tiny byte-vocabulary adapter for deterministic fixtures only.</summary>
public sealed class FixtureTokenizerAdapter : ITokenizerAdapter
{
    public TokenizerManifest Manifest { get; }
    public IReadOnlyList<byte> Vocabulary { get; }

    internal FixtureTokenizerAdapter(TokenizerManifest manifest, byte[] asset)
    {
        Manifest = manifest;
        if (asset.Length == 0) throw new InvalidDataException("Fixture vocabulary is empty.");
        Vocabulary = Array.AsReadOnly(asset.Distinct().Order().ToArray());
    }

    /// <summary>Fixture encoding: longest declared special token first, otherwise one vocabulary byte per token.</summary>
    public long CountTokens(string renderedInput)
    {
        ArgumentNullException.ThrowIfNull(renderedInput);
        var bytes = Encoding.UTF8.GetBytes(renderedInput);
        var specials = Manifest.SpecialTokens.OrderByDescending(s => Encoding.UTF8.GetByteCount(s))
            .ThenBy(s => s, StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes).ToArray();
        long count = 0;
        for (var offset = 0; offset < bytes.Length; count++)
        {
            var special = specials.FirstOrDefault(s => bytes.AsSpan(offset).StartsWith(s));
            if (special is not null) offset += special.Length;
            else
            {
                if (!Vocabulary.Contains(bytes[offset])) throw new NotSupportedException("Rendered input contains a byte outside the fixture vocabulary.");
                offset++;
            }
        }
        return count;
    }
}

/// <summary>Encodes verified complete cl100k_base assets without inference or downloads.</summary>
public sealed class TiktokenTokenizerAdapter : ITokenizerAdapter
{
    public TokenizerManifest Manifest { get; }
    public IReadOnlyDictionary<string, int> MergeableRanks { get; }
    private readonly TiktokenTokenizer? _encoder;
    private const string Cl100kVocabularySha256 = "223921b76ee99bde995b7ff738513eef100fb51d18c93597a113bcffe865b2a7";
    private static readonly string[] Cl100kSpecialTokens =
        ["<|endoftext|>", "<|fim_prefix|>", "<|fim_middle|>", "<|fim_suffix|>", "<|endofprompt|>"];

    internal TiktokenTokenizerAdapter(TokenizerManifest manifest, byte[] asset)
    {
        Manifest = manifest;
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var rankValues = new HashSet<int>();
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(asset));
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(' ');
            if (fields.Length != 2 || !int.TryParse(fields[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var rank))
                throw new InvalidDataException("Invalid tiktoken token/rank record.");
            byte[] token;
            try { token = Convert.FromBase64String(fields[0]); }
            catch (FormatException ex) { throw new InvalidDataException("Invalid tiktoken token encoding.", ex); }
            if (token.Length == 0 || !ranks.TryAdd(Convert.ToBase64String(token), rank) || !rankValues.Add(rank))
                throw new InvalidDataException("Tiktoken tokens and ranks must be nonempty and unique.");
        }
        if (ranks.Count == 0 || rankValues.Max() != ranks.Count - 1)
            throw new InvalidDataException("Tiktoken ranks must be contiguous from zero.");
        MergeableRanks = new ReadOnlyDictionary<string, int>(ranks);
        // Preserve metadata/rank readers for unsupported encodings and old incomplete assets.
        // Exact encoding requires the complete cl100k vocabulary and known special-token IDs.
        if (manifest.Encoding != "cl100k_base" || ranks.Count != 100256 ||
            !StringComparer.OrdinalIgnoreCase.Equals(manifest.Assets[0].Sha256, Cl100kVocabularySha256) ||
            manifest.SpecialTokens.Any(s => !Cl100kSpecialTokens.Contains(s, StringComparer.Ordinal)) ||
            Enumerable.Range(0, 256).Any(b => !ranks.ContainsKey(Convert.ToBase64String([(byte)b])))) return;
        // Use the already hash-verified bytes, never reopen the declared path or substitute built-in data.
        using var stream = new MemoryStream(asset, writable: false);
        _encoder = TiktokenTokenizer.CreateForModel("gpt-4", stream);
    }

    public long CountTokens(string renderedInput) => EncodeToIds(renderedInput).Count;

    /// <summary>Exact IDs from the pinned encoding; undeclared control tokens fail closed.</summary>
    public IReadOnlyList<int> EncodeToIds(string renderedInput)
    {
        ArgumentNullException.ThrowIfNull(renderedInput);
        if (_encoder is null) throw new NotSupportedException("Verified cl100k_base encoding is unavailable.");
        if (Cl100kSpecialTokens.Any(s => renderedInput.Contains(s, StringComparison.Ordinal) &&
            !Manifest.SpecialTokens.Contains(s, StringComparer.Ordinal)))
            throw new NotSupportedException("Rendered input contains an undeclared special token.");
        return _encoder.EncodeToIds(renderedInput);
    }
}
