using System.Text.Json.Serialization;

namespace SdevEng;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillCompatibilityEntry(string OldId, string OldPath, string CanonicalId,
    string CanonicalPath, string Version, string[] Aliases, string MigrationStatus);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillCompatibilityMap(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion, SkillCompatibilityEntry[] Skills)
{
    public void Validate()
    {
        if (Schema != "../schemas/skill-compatibility-map.schema.json" || SchemaVersion != 1 || Skills is null || Skills.Length == 0)
            throw new ArgumentException("Invalid skill compatibility map version or empty inventory.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in Skills)
        {
            if (skill is null || string.IsNullOrWhiteSpace(skill.CanonicalId) ||
                skill.CanonicalId.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) ||
                skill.CanonicalPath != $"plugins/sdeveng/skills/{skill.CanonicalId}/SKILL.md" ||
                string.IsNullOrWhiteSpace(skill.OldId) || string.IsNullOrWhiteSpace(skill.OldPath) ||
                !System.Text.RegularExpressions.Regex.IsMatch(skill.Version ?? "", @"^\d+\.\d+\.\d+$") ||
                skill.Aliases is null || skill.Aliases.Any(string.IsNullOrWhiteSpace) ||
                skill.MigrationStatus is not ("retained" or "extension-required" or "migration-required"))
                throw new ArgumentException("Invalid skill compatibility entry.");
            if (!identities.Add(skill.CanonicalId) || !paths.Add(skill.CanonicalPath))
                throw new ArgumentException("Duplicate canonical skill identity or path.");
            if (skill.MigrationStatus == "retained" && (skill.OldId != skill.CanonicalId || skill.OldPath != skill.CanonicalPath))
                throw new ArgumentException("Retained skill must preserve its identity and path.");
        }
        foreach (var skill in Skills)
        {
            if (skill.OldId != skill.CanonicalId && !skill.Aliases.Contains(skill.OldId, StringComparer.Ordinal))
                throw new ArgumentException("Old skill identity must remain a resolvable alias.");
            foreach (var alias in skill.Aliases)
                if (!identities.Add(alias)) throw new ArgumentException("Ambiguous skill alias.");
        }
    }

    public SkillCompatibilityEntry? Resolve(string identity)
    {
        Validate();
        return Skills.SingleOrDefault(s => s.CanonicalId == identity || s.Aliases.Contains(identity, StringComparer.Ordinal));
    }
}
