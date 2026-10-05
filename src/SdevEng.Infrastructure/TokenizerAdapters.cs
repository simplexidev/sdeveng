using System.Collections.ObjectModel;
using System.Text;
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
}

/// <summary>Loads pinned tiktoken base64-token/integer-rank assets; does not perform inference or counting.</summary>
public sealed class TiktokenTokenizerAdapter : ITokenizerAdapter
{
    public TokenizerManifest Manifest { get; }
    public IReadOnlyDictionary<string, int> MergeableRanks { get; }

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
    }
}
