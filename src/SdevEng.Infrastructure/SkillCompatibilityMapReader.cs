using System.Text.Json;
using System.Security.Cryptography;

namespace SdevEng;

public static class SkillCompatibilityMapReader
{
    public static SkillMetadata ReadSkillMetadata(string path)
    {
        return ParseMetadata(File.ReadAllText(path), path);
    }

    public static SkillMetadata ReadSkillFrontMatter(string path)
    {
        // Read bytes through the closing delimiter; do not prefetch instruction content.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        return ReadSkillFrontMatter(stream);
    }

    public static SkillMetadata ReadSkillFrontMatter(Stream stream)
    {
        var bytes = new List<byte>();
        var line = new List<byte>();
        while (stream.ReadByte() is var next && next >= 0)
        {
            bytes.Add((byte)next);
            if (next != '\n') { line.Add((byte)next); continue; }
            var delimiter = line.SequenceEqual(new byte[] { 45, 45, 45 });
            if (bytes.Count == line.Count + 1 && !delimiter) throw new ArgumentException("Skill front matter is missing.");
            if (delimiter && bytes.Count > 4)
                return ParseMetadata(System.Text.Encoding.UTF8.GetString(bytes.ToArray()), null);
            line.Clear();
        }
        if (line.SequenceEqual(new byte[] { 45, 45, 45 }) && bytes.Count > 3)
            return ParseMetadata(System.Text.Encoding.UTF8.GetString(bytes.ToArray()), null);
        throw new ArgumentException("Skill front matter is unterminated.");
    }

    private static SkillMetadata ParseMetadata(string text, string? path)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new ArgumentException("Skill front matter is missing.");
        var end = path is null ? text.LastIndexOf("\n---", StringComparison.Ordinal) : text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("Skill front matter is unterminated.");
        var fields = text[4..end].Split('\n').Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
        fields.TryGetValue("id", out var id);
        fields.TryGetValue("version", out var version);
        if ((id is null) != (version is null)) throw new ArgumentException("Skill id and version must be declared together.");
        if (id is not null && (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$") ||
                               !System.Text.RegularExpressions.Regex.IsMatch(version!, "^\\d+\\.\\d+\\.\\d+$")))
            throw new ArgumentException("Invalid skill identity or version.");
        SkillActivationCondition[]? activation = null;
        if (fields.TryGetValue("activation", out var activationJson))
        {
            if (activationJson.Length >= 2 && activationJson[0] == '\'' && activationJson[^1] == '\'')
                activationJson = activationJson[1..^1].Replace("''", "'", StringComparison.Ordinal);
            try
            {
                activation = JsonSerializer.Deserialize<SkillActivationCondition[]>(activationJson,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            catch (JsonException ex) { throw new ArgumentException("Invalid skill activation conditions.", ex); }
            if (activation is null || activation.Length == 0 || activation.Any(condition => condition is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(condition.Id ?? "", "^[a-z0-9]+(?:-[a-z0-9]+)*$") ||
                (condition.FrameworkVersion is not null && !System.Text.RegularExpressions.Regex.IsMatch(condition.FrameworkVersion,
                    "^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?$"))))
                throw new ArgumentException("Invalid skill activation conditions.");
        }
        string[]? supportedRoles = null;
        if (fields.TryGetValue("supportedRoles", out var rolesJson))
        {
            supportedRoles = ReadStringArray(rolesJson, "supported roles");
            if (supportedRoles.Length == 0 || supportedRoles.Distinct(StringComparer.Ordinal).Count() != supportedRoles.Length ||
                supportedRoles.Any(role => role is not ("planner" or "coder" or "test-author" or "reviewer" or "repair")))
                throw new ArgumentException("Invalid supported skill roles.");
        }
        string[]? requiredTools = null;
        if (fields.TryGetValue("requiredTools", out var toolsJson))
        {
            requiredTools = ReadStringArray(toolsJson, "required tools");
            if (requiredTools.Distinct(StringComparer.Ordinal).Count() != requiredTools.Length ||
                requiredTools.Any(tool => !System.Text.RegularExpressions.Regex.IsMatch(tool, "^[a-z0-9]+(?:-[a-z0-9]+)*$")))
                throw new ArgumentException("Invalid required skill tools.");
        }
        int? contextAllowance = null;
        if (fields.TryGetValue("contextAllowance", out var allowanceText))
        {
            if (!int.TryParse(allowanceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var allowance) || allowance <= 0)
                throw new ArgumentException("Invalid skill context allowance.");
            contextAllowance = allowance;
        }
        SkillResource[]? resources = null;
        if (fields.TryGetValue("resources", out var resourcesJson))
        {
            if (resourcesJson.Length >= 2 && resourcesJson[0] == '\'' && resourcesJson[^1] == '\'')
                resourcesJson = resourcesJson[1..^1].Replace("''", "'", StringComparison.Ordinal);
            try { resources = JsonSerializer.Deserialize<SkillResource[]>(resourcesJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }); }
            catch (JsonException ex) { throw new ArgumentException("Invalid skill resources.", ex); }
            if (resources is null || resources.Any(resource => resource is null) ||
                resources.Select(resource => resource.Path).Distinct(StringComparer.Ordinal).Count() != resources.Length ||
                resources.Any(resource => !System.Text.RegularExpressions.Regex.IsMatch(resource.Type ?? "", "^[a-z][a-z0-9-]*$") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(resource.Hash ?? "", "^sha256:[0-9a-f]{64}$")))
                throw new ArgumentException("Invalid skill resources.");
        }
        return new SkillMetadata(fields.GetValueOrDefault("name") ?? "", id, version, activation, supportedRoles, requiredTools, contextAllowance, resources);
    }

    internal static bool IsSafeResourcePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) && path.Replace('\\', '/').Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..") &&
        !path.Contains('\\');

    private static string[] ReadStringArray(string value, string description)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            value = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? throw new ArgumentException($"Invalid {description}.");
        }
        catch (JsonException ex) { throw new ArgumentException($"Invalid {description}.", ex); }
    }

    public static SkillCompatibilityMap Read(string root)
    {
        var map = ReadMap(root);
        ValidateInventory(root, map, deep: true);
        ValidateResources(root, map.Skills.Select(skill => ReadSkillMetadata(Path.Combine(root, skill.CanonicalPath))));
        return map;
    }

    private static SkillCompatibilityMap ReadMap(string root)
    {
        var map = JsonSerializer.Deserialize<SkillCompatibilityMap>(
            File.ReadAllText(Path.Combine(root, "config/skill-compatibility-map.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new ArgumentException("Empty skill compatibility map.");
        map.Validate();
        return map;
    }

    public static SkillMetadata[] ReadMetadataIndex(string root)
    {
        var map = ReadMap(root);
        var metadata = map.Skills.Select(skill => ReadSkillFrontMatter(Path.Combine(root, skill.CanonicalPath))).ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < metadata.Length; i++)
        {
            var entry = map.Skills[i];
            var item = metadata[i];
            var resolved = map.Resolve(item.Id ?? item.Name);
            if (resolved is null) throw new ArgumentException("Unresolved skill identity: " + (item.Id ?? item.Name));
            if (!identities.Add(resolved.CanonicalId)) throw new ArgumentException("Duplicate skill identity: " + resolved.CanonicalId);
            metadata[i] = item with { Id = resolved.CanonicalId, Version = item.Version ?? resolved.Version };
            if (resolved != entry || metadata[i].Version != entry.Version)
                throw new ArgumentException("Skill metadata differs from compatibility entry: " + entry.CanonicalId);
        }
        ValidateInventory(root, map, deep: false);
        return metadata;
    }

    public static void ValidateRequiredTools(string root, IEnumerable<SkillMetadata> metadata, List<string> errors)
    {
        using var tools = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/agent-tool-contracts.json")));
        var names = tools.RootElement.GetProperty("toolDescriptors").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var skill in metadata)
            foreach (var tool in skill.RequiredTools ?? [])
                if (!names.Contains(tool)) errors.Add($"Unknown required tool for skill {skill.Id}: {tool}");
    }

    public static void ValidateResources(string root, IEnumerable<SkillMetadata> metadata, bool verifyHashes = true)
    {
        var skillsRoot = Path.GetFullPath(Path.Combine(root, "plugins", "sdeveng", "skills"));
        foreach (var skill in metadata)
        {
            var skillDirectory = Path.GetFullPath(Path.Combine(skillsRoot, skill.Id ?? skill.Name));
            if (!skillDirectory.StartsWith(skillsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Skill directory is outside the skill inventory: " + skill.Name);
            EnsureNoLinks(skillsRoot, skillDirectory);
            foreach (var resource in skill.Resources ?? [])
            {
                if (!IsSafeResourcePath(resource.Path)) throw new ArgumentException("Invalid skill resource path: " + resource.Path);
                var fullPath = Path.GetFullPath(Path.Combine(skillDirectory, resource.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(skillDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Skill resource must exist under its skill directory: " + resource.Path);
                EnsureNoLinks(skillDirectory, fullPath);
                if (!File.Exists(fullPath)) throw new ArgumentException("Skill resource must exist under its skill directory: " + resource.Path);
                if (!verifyHashes) continue;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                var actualHash = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (actualHash != resource.Hash) throw new ArgumentException("Skill resource hash mismatch: " + resource.Path);
            }
        }
    }

    internal static void EnsureNoLinks(string boundary, string path)
    {
        for (var current = Path.GetFullPath(path); current.Length >= boundary.Length;
             current = Path.GetDirectoryName(current) ?? string.Empty)
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new ArgumentException("Skill resource paths must not contain links: " + Path.GetRelativePath(boundary, path));
            if (string.Equals(current, boundary, StringComparison.Ordinal)) return;
        }
        throw new ArgumentException("Skill resource is outside its skill directory.");
    }

    private static void ValidateInventory(string root, SkillCompatibilityMap map, bool deep)
    {
        var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/plugin.json")));
        using (version)
        {
            foreach (var skill in map.Skills)
            {
                var body = Path.Combine(root, skill.CanonicalPath);
                if (skill.Version != version.RootElement.GetProperty("version").GetString() || !File.Exists(body) ||
                    !File.Exists(Path.Combine(Path.GetDirectoryName(body)!, "agents/openai.yaml")))
                    throw new ArgumentException("Unresolved skill compatibility entry: " + skill.CanonicalId);
                var metadata = deep ? ReadSkillMetadata(body) : ReadSkillFrontMatter(body);
                if ((deep ? metadata.Name != skill.CanonicalId : map.Resolve(metadata.Name)?.CanonicalId != skill.CanonicalId) || (metadata.Id is not null &&
                    (metadata.Id != skill.CanonicalId || metadata.Version != skill.Version)))
                    throw new ArgumentException("Skill metadata differs from compatibility entry: " + skill.CanonicalId);
            }
        }
        var discovered = Directory.GetDirectories(Path.Combine(root, "plugins/sdeveng/skills")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        if (!discovered.SequenceEqual(map.Skills.Select(s => s.CanonicalId).Order(StringComparer.Ordinal)))
            throw new ArgumentException("Skill compatibility inventory differs from discovery.");
    }
}

public sealed record SkillMetadata(string Name, string? Id, string? Version,
    SkillActivationCondition[]? Activation = null, string[]? SupportedRoles = null, string[]? RequiredTools = null,
    int? ContextAllowance = null, SkillResource[]? Resources = null);

public sealed record SkillActivationCondition(string Id, string? FrameworkVersion = null);
public sealed record SkillResource(string Path, string Type, string Hash);
