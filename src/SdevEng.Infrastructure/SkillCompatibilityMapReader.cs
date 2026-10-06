using System.Text.Json;

namespace SdevEng;

public static class SkillCompatibilityMapReader
{
    public static SkillMetadata ReadSkillMetadata(string path)
    {
        var text = File.ReadAllText(path);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new ArgumentException("Skill front matter is missing.");
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
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
        return new SkillMetadata(fields.GetValueOrDefault("name") ?? "", id, version, activation);
    }

    public static SkillCompatibilityMap Read(string root)
    {
        var map = JsonSerializer.Deserialize<SkillCompatibilityMap>(
            File.ReadAllText(Path.Combine(root, "config/skill-compatibility-map.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new ArgumentException("Empty skill compatibility map.");
        map.Validate();
        var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/plugin.json")));
        using (version)
        {
            foreach (var skill in map.Skills)
            {
                var body = Path.Combine(root, skill.CanonicalPath);
                if (skill.Version != version.RootElement.GetProperty("version").GetString() || !File.Exists(body) ||
                    !File.Exists(Path.Combine(Path.GetDirectoryName(body)!, "agents/openai.yaml")))
                    throw new ArgumentException("Unresolved skill compatibility entry: " + skill.CanonicalId);
                var metadata = ReadSkillMetadata(body);
                if (metadata.Name != skill.CanonicalId || (metadata.Id is not null &&
                    (metadata.Id != skill.CanonicalId || metadata.Version != skill.Version)))
                    throw new ArgumentException("Skill metadata differs from compatibility entry: " + skill.CanonicalId);
            }
        }
        var discovered = Directory.GetDirectories(Path.Combine(root, "plugins/sdeveng/skills")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        if (!discovered.SequenceEqual(map.Skills.Select(s => s.CanonicalId).Order(StringComparer.Ordinal)))
            throw new ArgumentException("Skill compatibility inventory differs from discovery.");
        return map;
    }
}

public sealed record SkillMetadata(string Name, string? Id, string? Version,
    SkillActivationCondition[]? Activation = null);

public sealed record SkillActivationCondition(string Id, string? FrameworkVersion = null);
