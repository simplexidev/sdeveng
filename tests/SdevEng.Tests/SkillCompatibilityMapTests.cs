using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class SkillCompatibilityMapTests
{
    static string Root => AgentTool.FindToolkit();
    static SkillCompatibilityEntry Entry => new("prepare-commit", "plugins/sdeveng/skills/prepare-commit/SKILL.md",
        "prepare-commit", "plugins/sdeveng/skills/prepare-commit/SKILL.md", "3.0.0",
        ["sdeveng-engineering-toolkit:prepare-commit"], "retained");
    static SkillCompatibilityMap Minimal => new("../schemas/skill-compatibility-map.schema.json", 1, [Entry]);

    [Fact]
    public void ExistingSkillResolvesThroughCurrentMetadataValidation()
    {
        var map = SkillCompatibilityMapReader.Read(Root);
        Assert.Equal("plugins/sdeveng/skills/prepare-commit/SKILL.md", map.Resolve("prepare-commit")!.CanonicalPath);
        Assert.Equal(map.Resolve("prepare-commit"), map.Resolve("sdeveng-engineering-toolkit:prepare-commit"));
        Assert.Null(map.Resolve("unknown"));
        var errors = new List<string>();
        PluginManifests.Validate(Root, errors);
        Assert.Empty(errors);
        var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-compatibility-map.schema.json"));
        Assert.True(schema.Evaluate(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/skill-compatibility-map.json")))!).IsValid);
    }

    [Fact]
    public void SkillIdentityVersionVariantAndLegacyFrontMatterAreCompatible()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Path.Combine(root, "legacy.md");
            File.WriteAllText(legacy, "---\nname: prepare-commit\ndescription: Legacy skill description.\n---\nBody\n");
            Assert.Equal(new SkillMetadata("prepare-commit", null, null), SkillCompatibilityMapReader.ReadSkillMetadata(legacy));
            var versioned = Path.Combine(root, "versioned.md");
            File.WriteAllText(versioned, "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\ndescription: Versioned skill description.\n---\nBody\n");
            Assert.Equal(new SkillMetadata("prepare-commit", "prepare-commit", "3.0.0"), SkillCompatibilityMapReader.ReadSkillMetadata(versioned));
            var malformed = Path.Combine(root, "malformed.md");
            File.WriteAllText(malformed, "---\nname: prepare-commit\nid: bad_id\nversion: x\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-metadata.schema.json"));
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","id":"prepare-commit","version":"3.0.0"}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","id":"bad_id","version":"x"}""")!).IsValid);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MinimalContractAndMigrationAliasRemainCompatible()
    {
        Minimal.Validate();
        var migrated = Minimal with { Skills = [Entry with { OldId = "old-name", Aliases = ["old-name"], MigrationStatus = "migration-required" }] };
        Assert.Equal("prepare-commit", migrated.Resolve("old-name")!.CanonicalId);
        var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-compatibility-map.schema.json"));
        var node = JsonSerializer.SerializeToNode(Minimal, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.True(schema.Evaluate(node).IsValid);
        node["skills"]![0]!["migrationStatus"] = "unknown";
        Assert.False(schema.Evaluate(node).IsValid);
    }

    [Fact]
    public void DuplicateCanonicalIdentityAndAmbiguousAliasesReject()
    {
        Assert.Throws<ArgumentException>(() => (Minimal with { Skills = [Entry, Entry] }).Validate());
        Assert.Throws<ArgumentException>(() => (Minimal with { Skills = [Entry with { Aliases = ["prepare-commit"] }] }).Validate());
        Assert.Throws<ArgumentException>(() => (Minimal with { Skills = [Entry with { OldId = "old", MigrationStatus = "migration-required" }] }).Validate());
        Assert.Throws<ArgumentException>(() => (Minimal with { SchemaVersion = 2 }).Validate());
    }

    [Fact]
    public void OwningManifestValidationRejectsDuplicateMapBeforeDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"),
                JsonSerializer.Serialize(Minimal with { Skills = [Entry, Entry] },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var errors = new List<string>();
            PluginManifests.Validate(root, errors);
            Assert.Single(errors);
            Assert.Contains("Duplicate canonical skill identity", errors[0], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }
}
