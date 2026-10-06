using System.Security.Cryptography;
using System.Text;

namespace SdevEng;

public sealed partial class SkillActivationService
{
    /// <summary>Loads only the requested canonical skill's instruction body after activation.</summary>
    public Task<SkillLoadResult> LoadInstructionsAsync(string toolkitRoot, SkillActivationContext context,
        string skillId, CancellationToken cancellationToken = default) =>
        LoadAsync(toolkitRoot, context, skillId, null, cancellationToken);

    /// <summary>Loads one declared reference, never following links in its content.</summary>
    public Task<SkillLoadResult> LoadReferenceAsync(string toolkitRoot, SkillActivationContext context,
        string skillId, string referencePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        return LoadAsync(toolkitRoot, context, skillId, referencePath, cancellationToken);
    }

    private async Task<SkillLoadResult> LoadAsync(string toolkitRoot, SkillActivationContext context,
        string skillId, string? referencePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        var skill = (await ActivateAsync(toolkitRoot, context, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Id == skillId);
        return await LoadActivatedAsync(toolkitRoot, skillId, skill, referencePath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SkillLoadResult> LoadActivatedAsync(string toolkitRoot, string skillId,
        SkillMetadata? skill, string? referencePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SkillLoadResult Omit(string reason) => new(skillId, referencePath, "omitted", null, reason, 0, 0);
        if (skill is null) return Omit("skill-not-activated");
        var resource = referencePath is null ? null :
            (skill.Resources ?? []).SingleOrDefault(item => item.Path == referencePath && item.Type == "reference");
        if (referencePath is not null && resource is null) return Omit("unknown-reference");
        if (referencePath is not null && !SkillCompatibilityMapReader.IsSafeResourcePath(referencePath))
            return Omit("unsafe-path");

        var root = Path.GetFullPath(toolkitRoot);
        var directory = Path.Combine(root, "plugins", "sdeveng", "skills", skillId);
        var path = Path.Combine(directory, referencePath ?? "SKILL.md");
        try
        {
            SkillCompatibilityMapReader.EnsureNoLinks(root, path);
        }
        catch (ArgumentException) { return Omit("unsafe-path"); }
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (resource is not null && resource.Hash != "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())
                return Omit("hash-mismatch");
            var content = new UTF8Encoding(false, true).GetString(bytes);
            if (referencePath is null)
            {
                var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
                if (!content.StartsWith("---\n", StringComparison.Ordinal) || end < 0 ||
                    (end + 4 < content.Length && content[end + 4] != '\n'))
                    return Omit("invalid-instructions");
                content = content[Math.Min(end + 5, content.Length)..];
            }
            return new(skillId, referencePath, "loaded", content, null, Encoding.UTF8.GetByteCount(content), content.Length);
        }
        catch (IOException) { return Omit("content-unavailable"); }
        catch (UnauthorizedAccessException) { return Omit("content-unavailable"); }
        catch (DecoderFallbackException) { return Omit("invalid-utf8"); }
    }
}
