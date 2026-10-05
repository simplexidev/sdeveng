using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Loads tokenizer metadata and verifies local assets without loading weights or downloading files.</summary>
public sealed class TokenizerRegistry(string assetRoot)
{
    private readonly Dictionary<string, TokenizerManifest> _manifests = new(StringComparer.Ordinal);
    private readonly string _assetRoot = Path.GetFullPath(assetRoot);

    public void Register(TokenizerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (_manifests.ContainsKey(manifest.Id)) throw new ArgumentException("Tokenizer identity is already registered.", nameof(manifest));
        foreach (var asset in manifest.Assets)
        {
            var fullPath = Path.GetFullPath(Path.Combine(_assetRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(_assetRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Tokenizer asset escapes the configured root.", nameof(manifest));
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Tokenizer asset is unavailable.", fullPath);
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(asset.Sha256)))
                throw new InvalidDataException($"Tokenizer asset checksum mismatch: {asset.Path}");
        }
        _manifests.Add(manifest.Id, manifest);
    }

    public TokenizerManifest Get(string id) => _manifests.TryGetValue(id, out var manifest)
        ? manifest : throw new KeyNotFoundException($"Tokenizer is unavailable: {id}");

    public static TokenizerManifest ReadMetadata(string path)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new JsonException("Tokenizer manifest is empty.");
        // Version 1 metadata predates modelFamily; preserve existing manifests with an explicit legacy identity.
        json["modelFamily"] ??= "legacy-unspecified";
        var manifest = json.Deserialize<TokenizerManifest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Tokenizer manifest is empty.");
        manifest.Validate();
        return manifest;
    }
}
