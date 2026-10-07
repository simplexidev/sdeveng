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

    sealed class HeaderOnlyStream(byte[] header) : MemoryStream(header)
    {
        public override int ReadByte() => Position == Length
            ? throw new InvalidOperationException("Instruction body must not be read.") : base.ReadByte();
    }

    [Fact]
    public void FrontMatterReaderStopsExactlyAtClosingDelimiter()
    {
        using var stream = new HeaderOnlyStream(System.Text.Encoding.UTF8.GetBytes("---\nname: prepare-commit\n---\n"));
        Assert.Equal("prepare-commit", SkillCompatibilityMapReader.ReadSkillFrontMatter(stream).Name);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void ValidationCallerRejectsDuplicateIdentityAndUnknownTool()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in SafeFiles.Enumerate(Root))
            {
                var destination = Path.Combine(root, Path.GetRelativePath(Root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var path = Path.Combine(root, "plugins/sdeveng/skills/prepare-commit/SKILL.md");
            var original = File.ReadAllText(path);
            File.WriteAllText(path, original.Replace("\nname: prepare-commit\n", "\nname: prepare-commit\nid: ci-triage\nversion: 3.0.0\n", StringComparison.Ordinal));
            var duplicate = Validation.Run(root);
            Assert.Equal(1, duplicate.ExitCode);
            Assert.Contains("Duplicate skill identity", JsonSerializer.Serialize(duplicate));
            File.WriteAllText(path, original.Replace("\nname: prepare-commit\n", "\nname: prepare-commit\nrequiredTools: '[\"unknown-tool\"]'\n", StringComparison.Ordinal));
            var unknown = Validation.Run(root);
            Assert.Equal(1, unknown.ExitCode);
            Assert.Contains("Unknown required tool", JsonSerializer.Serialize(unknown));
            File.WriteAllText(path, original.Replace("\nname: prepare-commit\n", $"\nname: prepare-commit\nresources: '[{{\"path\":\"missing.md\",\"type\":\"reference\",\"hash\":\"sha256:{new string('0', 64)}\"}}]'\n", StringComparison.Ordinal));
            var missing = Validation.Run(root);
            Assert.Equal(1, missing.ExitCode);
            Assert.Contains("Skill resource must exist", JsonSerializer.Serialize(missing));
            File.WriteAllText(path, original.Replace("\nname: prepare-commit\n", "\nname: prepare-commit\nsupportedRoles: '[\"unknown\"]'\n", StringComparison.Ordinal));
            Assert.Contains("Invalid supported skill roles", JsonSerializer.Serialize(Validation.Run(root)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MetadataIndexProjectsLegacyIdentityWithoutLoadingResourcesOrBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var directory = Path.Combine(root, "plugins/sdeveng/skills/prepare-commit");
        Directory.CreateDirectory(Path.Combine(directory, "agents"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(Minimal,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/plugin.json"), "{\"version\":\"3.0.0\"}");
            File.WriteAllText(Path.Combine(directory, "agents/openai.yaml"), "interface: {}");
            var path = Path.Combine(directory, "SKILL.md");
            File.WriteAllText(path, $"---\nname: prepare-commit\nresources: '[{{\"path\":\"missing.md\",\"type\":\"reference\",\"hash\":\"sha256:{new string('0', 64)}\"}}]'\n---\n" + new string('x', 2_000_000));
            var item = Assert.Single(SkillCompatibilityMapReader.ReadMetadataIndex(root));
            Assert.Equal("prepare-commit", item.Id);
            Assert.Equal("3.0.0", item.Version);
            Assert.Equal("missing.md", Assert.Single(item.Resources!).Path);
            var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-metadata.schema.json"));
            Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(item, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            })!).IsValid);
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.Read(root));
            File.WriteAllText(path, "---\nname: sdeveng-engineering-toolkit:prepare-commit\n---\nBody\n");
            Assert.Equal("prepare-commit", Assert.Single(SkillCompatibilityMapReader.ReadMetadataIndex(root)).Id);
            File.WriteAllText(path, "---\nname: prepare-commit\n---\n");
            var second = Entry with { OldId = "other", CanonicalId = "other", OldPath = "plugins/sdeveng/skills/other/SKILL.md", CanonicalPath = "plugins/sdeveng/skills/other/SKILL.md", Aliases = [] };
            Directory.CreateDirectory(Path.Combine(root, "plugins/sdeveng/skills/other"));
            File.WriteAllText(Path.Combine(root, second.CanonicalPath), "---\nname: other\nid: prepare-commit\nversion: 3.0.0\n---\n");
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(Minimal with { Skills = [Entry, second] },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            Assert.Contains("Duplicate skill identity", Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadMetadataIndex(root)).Message);
            var errors = new List<string>();
            PluginManifests.Validate(root, errors);
            Assert.Contains(errors, error => error.Contains("Duplicate skill identity", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("supportedRoles: '[\"planner\",\"coder\",\"test-author\",\"reviewer\",\"repair\"]'", null)]
    [InlineData("supportedRoles: '[\"Coder\"]'", "Invalid supported skill roles")]
    [InlineData("supportedRoles: '[\"coder\",\"coder\"]'", "Invalid supported skill roles")]
    [InlineData("requiredTools: '[\"unknown-tool\"]'", "Unknown required tool")]
    [InlineData("resources: '[{\"path\":\"reference.md\",\"type\":\"reference\",\"hash\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]'", null)]
    [InlineData("resources: '[{\"path\":\"missing.md\",\"type\":\"reference\",\"hash\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]'", "Skill resource must exist")]
    public async Task CatalogNormalDispatchValidatesMetadataWithoutLoadingBodies(string fields, string? error)
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-catalog-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins/sdeveng/skills/prepare-commit");
        Directory.CreateDirectory(Path.Combine(directory, "agents"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(Minimal, AgentTool.Json));
            File.Copy(Path.Combine(Root, "config/agent-tool-contracts.json"), Path.Combine(root, "config/agent-tool-contracts.json"));
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/plugin.json"), "{\"version\":\"3.0.0\"}");
            File.WriteAllText(Path.Combine(directory, "agents/openai.yaml"), "interface: {}");
            // Deliberately differs from the declared hash: catalog checks existence only.
            File.WriteAllText(Path.Combine(directory, "reference.md"), "REFERENCE_SENTINEL");
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: prepare-commit\n{fields}\n---\nINSTRUCTION_SENTINEL");
            var result = await CommandTestRuntime.Execute(Cli.Parse(["skills", "list", "--json"]), root, root, new(new(), new(), new(), new()));
            var json = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            Assert.Equal(error is null ? 0 : 1, result.ExitCode);
            Assert.DoesNotContain("INSTRUCTION_SENTINEL", json.ToJsonString());
            Assert.DoesNotContain("REFERENCE_SENTINEL", json.ToJsonString());
            if (error is not null) Assert.Contains(error, json["diagnostics"]!.ToJsonString());
            else Assert.Empty(json["diagnostics"]!.AsArray());
            var options = new EvaluationOptions();
            var metadataPath = Path.Combine(Root, "schemas/skill-metadata.schema.json");
            options.SchemaRegistry.Register(JsonSchema.FromFile(metadataPath));
            Assert.True(JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-catalog.schema.json")).Evaluate(json, options).IsValid);
        }
        finally { Directory.Delete(root, true); }
    }

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
        var skillDirectory = Path.Combine(root, "plugins", "sdeveng", "skills", "prepare-commit");
        Directory.CreateDirectory(skillDirectory);
        try
        {
            var legacy = Path.Combine(skillDirectory, "legacy.md");
            File.WriteAllText(legacy, "---\nname: prepare-commit\ndescription: Legacy skill description.\n---\nBody\n");
            Assert.Equal(new SkillMetadata("prepare-commit", null, null), SkillCompatibilityMapReader.ReadSkillMetadata(legacy));
            var versioned = Path.Combine(skillDirectory, "versioned.md");
            File.WriteAllText(versioned, "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\ndescription: Versioned skill description.\n---\nBody\n");
            Assert.Equal(new SkillMetadata("prepare-commit", "prepare-commit", "3.0.0"), SkillCompatibilityMapReader.ReadSkillMetadata(versioned));
            File.WriteAllText(versioned, "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\nactivation: '[{\"id\":\"dotnet-build\",\"frameworkVersion\":\"10.0.0\"}]'\n---\nBody\n");
            var activated = SkillCompatibilityMapReader.ReadSkillMetadata(versioned);
            Assert.Equal(new SkillActivationCondition("dotnet-build", "10.0.0"), Assert.Single(activated.Activation!));
            File.WriteAllText(versioned, "---\nname: prepare-commit\nsupportedRoles: '[\"coder\",\"reviewer\"]'\nrequiredTools: '[\"git-status\",\"read-file\"]'\n---\nBody\n");
            var capabilities = SkillCompatibilityMapReader.ReadSkillMetadata(versioned);
            Assert.Equal(new[] { "coder", "reviewer" }, capabilities.SupportedRoles);
            Assert.Equal(new[] { "git-status", "read-file" }, capabilities.RequiredTools);
            var reference = Path.Combine(skillDirectory, "reference.md");
            File.WriteAllText(reference, "Reference content\n");
            var hash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(reference))).ToLowerInvariant();
            File.WriteAllText(versioned, $"---\nname: prepare-commit\ncontextAllowance: 2400\nresources: '[{{\"path\":\"reference.md\",\"type\":\"reference\",\"hash\":\"{hash}\"}}]'\n---\nBody\n");
            var budgetAndResources = SkillCompatibilityMapReader.ReadSkillMetadata(versioned);
            Assert.Equal(2400, budgetAndResources.ContextAllowance);
            Assert.Equal(new SkillResource("reference.md", "reference", hash), Assert.Single(budgetAndResources.Resources!));
            var malformed = Path.Combine(skillDirectory, "malformed.md");
            File.WriteAllText(malformed, "---\nname: prepare-commit\nid: bad_id\nversion: x\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            File.WriteAllText(malformed, "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\nactivation: '[{\"id\":\"bad_id\"}]'\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            File.WriteAllText(malformed, "---\nname: prepare-commit\nsupportedRoles: '[\"unknown\"]'\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            File.WriteAllText(malformed, "---\nname: prepare-commit\nrequiredTools: '[\"bad_id\"]'\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            File.WriteAllText(malformed, "---\nname: prepare-commit\ncontextAllowance: 0\n---\nBody\n");
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSkillMetadata(malformed));
            File.WriteAllText(malformed, $"---\nname: prepare-commit\nresources: '[{{\"path\":\"../escape.md\",\"type\":\"reference\",\"hash\":\"{hash}\"}}]'\n---\nBody\n");
            var traversal = SkillCompatibilityMapReader.ReadSkillMetadata(malformed);
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ValidateResources(root, [traversal]));
            File.WriteAllText(malformed, $"---\nname: prepare-commit\nresources: '[{{\"path\":\"reference.md\",\"type\":\"reference\",\"hash\":\"sha256:{new string('0', 64)}\"}}]'\n---\nBody\n");
            var wrongHash = SkillCompatibilityMapReader.ReadSkillMetadata(malformed);
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ValidateResources(root, [wrongHash]));
            var linked = Path.Combine(skillDirectory, "linked.md");
            File.CreateSymbolicLink(linked, reference);
            File.WriteAllText(malformed, $"---\nname: prepare-commit\nresources: '[{{\"path\":\"linked.md\",\"type\":\"reference\",\"hash\":\"{hash}\"}}]'\n---\nBody\n");
            var linkedResource = SkillCompatibilityMapReader.ReadSkillMetadata(malformed);
            Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ValidateResources(root, [linkedResource]));
            var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/skill-metadata.schema.json"));
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","id":"prepare-commit","version":"3.0.0"}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","id":"bad_id","version":"x"}""")!).IsValid);
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","activation":[{"id":"dotnet-build","frameworkVersion":"10.0.0"}]}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","activation":[{"id":"bad_id"}]}""")!).IsValid);
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","supportedRoles":["coder","reviewer"],"requiredTools":["git-status"]}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","supportedRoles":["unknown"]}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","requiredTools":["bad_id"]}""")!).IsValid);
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"contextAllowance":2400,"resources":[{"path":"references/guide.md","type":"reference","hash":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"contextAllowance":0}""")!).IsValid);
            Assert.False(schema.Evaluate(JsonNode.Parse("""{"resources":[{"path":"../guide.md","type":"reference","hash":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""")!).IsValid);
            var qualification = new SkillQualification("family", "revision-1", "coder", "3.0.0", true, 12,
                "tokenizer", "tok-1", "template", "template-1", ["no-secrets"], ["read-file"], ["preserve-errors"]);
            var profile = new SkillProfile("family", "revision-1", "coder", "3.0.0", "Concise qualified body.",
                ["no-secrets"], ["read-file"], ["preserve-errors"], qualification);
            File.WriteAllText(versioned, "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\nprofiles: '" +
                JsonSerializer.Serialize(new[] { profile }, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace("'", "''", StringComparison.Ordinal) + "'\n---\nCanonical body.\n");
            var profiled = SkillCompatibilityMapReader.ReadSkillMetadata(versioned);
            Assert.Equal("Concise qualified body.", SkillProfile.Select(profiled, "family", "revision-1", "coder", "3.0.0", qualification));
            Assert.Equal("prepare-commit", SkillProfile.Select(profiled, "family", "stale", "coder", "3.0.0", qualification));
            Assert.Equal("prepare-commit", SkillProfile.Select(profiled, "family", "revision-1", "coder", "3.0.0", null));
            Assert.Throws<ArgumentException>(() => (profile with { RequiredTools = ["write-file"] }).Validate("3.0.0"));
            var profileNode = JsonSerializer.SerializeToNode(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","id":"prepare-commit","version":"3.0.0","profiles":[""" + profileNode.ToJsonString() + "]}")!).IsValid);
            profileNode["qualification"]!["passed"] = false;
            Assert.True(schema.Evaluate(JsonNode.Parse("""{"name":"prepare-commit","profiles":[""" + profileNode.ToJsonString() + "]}")!).IsValid);
            Assert.Throws<ArgumentException>(() => (profile with { Qualification = qualification with { Passed = false } }).Validate("3.0.0"));
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
