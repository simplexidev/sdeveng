using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Loads tokenizer metadata and verifies local assets without loading weights or downloading files.</summary>
public sealed class TokenizerRegistry(string assetRoot)
{
    private readonly Dictionary<string, TokenizerManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITokenizerAdapter> _adapters = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, Func<TokenizerManifest, byte[], ITokenizerAdapter>> Factories =
        new Dictionary<string, Func<TokenizerManifest, byte[], ITokenizerAdapter>>(StringComparer.Ordinal)
        {
            [TokenizerAdapterId.Fixture] = (manifest, asset) => new FixtureTokenizerAdapter(manifest, asset),
            [TokenizerAdapterId.Tiktoken] = (manifest, asset) => new TiktokenTokenizerAdapter(manifest, asset)
        };
    private readonly string _assetRoot = Path.GetFullPath(assetRoot);

    public void Register(TokenizerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (_manifests.ContainsKey(manifest.Id)) throw new ArgumentException("Tokenizer identity is already registered.", nameof(manifest));
        if (!Factories.TryGetValue(manifest.Adapter, out var factory)) throw new ArgumentException("Unsupported tokenizer adapter.", nameof(manifest));
        if (manifest.Assets.Count != 1) throw new ArgumentException("Supported adapters require one vocabulary asset.", nameof(manifest));
        if (manifest.Family == TokenizerAssetFamily.Tiktoken &&
            !manifest.Revision.AsSpan(7).Equals(manifest.Assets[0].Sha256.AsSpan(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Tiktoken revision must pin the vocabulary asset checksum.", nameof(manifest));
        manifest = manifest with { Assets = Array.AsReadOnly(manifest.Assets.ToArray()), SpecialTokens = Array.AsReadOnly(manifest.SpecialTokens.ToArray()) };
        var assets = VerifyAssets(manifest);
        var adapter = factory(manifest, assets[0]);
        _manifests.Add(manifest.Id, manifest);
        _adapters.Add(manifest.Id, adapter);
    }

    /// <summary>Normal metadata-to-verified-adapter registration boundary, including legacy metadata replay.</summary>
    public ITokenizerAdapter RegisterFile(string path)
    {
        var manifest = ReadMetadata(path);
        Register(manifest);
        return Resolve(manifest.Id);
    }

    /// <summary>Checks manifest and tokenizer asset availability through normal registry resolution.</summary>
    public TokenizerAvailabilityResult CheckAvailability(string manifestPath)
    {
        try
        {
            var manifest = ReadMetadata(manifestPath);
            Register(manifest);
            _ = Resolve(manifest.Id);
            return new TokenizerAvailabilityResult(true, manifest.Id, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or NotSupportedException)
        {
            return new TokenizerAvailabilityResult(false, null, ex.Message);
        }
    }

    public ITokenizerAdapter Resolve(string id)
    {
        VerifyAssets(Get(id));
        return _adapters[id];
    }

    private List<byte[]> VerifyAssets(TokenizerManifest manifest)
    {
        var assets = new List<byte[]>();
        foreach (var asset in manifest.Assets)
        {
            var fullPath = Path.GetFullPath(Path.Combine(_assetRoot, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(_assetRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Tokenizer asset escapes the configured root.", nameof(manifest));
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Tokenizer asset is unavailable.", fullPath);
            var bytes = File.ReadAllBytes(fullPath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(asset.Sha256)))
                throw new InvalidDataException($"Tokenizer asset checksum mismatch: {asset.Path}");
            assets.Add(bytes);
        }
        return assets;
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
