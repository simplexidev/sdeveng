using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class FrameworkProvenanceTests
{
    static string Root => AgentTool.FindToolkit();
    static JsonObject Legacy() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tests/SdevEng.Tests/Fixtures/FrameworkProvenance/legacy-v2.json")))!.AsObject();
    static JsonObject Current() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "upstream/dotnet-skills.json")))!.AsObject();
    static bool Valid(JsonNode manifest) => JsonSchema.FromFile(Path.Combine(Root, "schemas/dotnet-skills-provenance.schema.json"))
        .Evaluate(manifest, new() { RequireFormatValidation = true }).IsValid;

    [Fact]
    public async Task ExistingCommandReadsBothVersionsWithoutWritingOrRefreshing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "framework-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "upstream"));
        try
        {
            var path = Path.Combine(directory, "upstream/dotnet-skills.json");
            foreach (var manifest in new[] { Legacy(), Current() })
            {
                var original = manifest.ToJsonString();
                File.WriteAllText(path, original);
                var result = await CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", "status", "--dry-run"]),
                    directory, directory, new Settings(new(), new(), new(), new()));
                Assert.Equal(0, result.ExitCode);
                var data = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
                Assert.Equal(manifest["snapshot"]!["commit"]!.GetValue<string>(), data["pinned"]!.GetValue<string>());
                Assert.Equal(original, File.ReadAllText(path));
                Assert.False(Directory.Exists(Path.Combine(directory, ".agent-tool")));
            }
            var invalid = Current();
            invalid["frameworks"]![0]!["sourceHash"] = "bad";
            File.WriteAllText(path, invalid.ToJsonString());
            await Assert.ThrowsAsync<FormatException>(() => DotnetSkillsDrift.Run(directory, Path.Combine(directory, "artifacts"), "status", true));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyAndCurrentPreserveEveryHistoricalFactWithoutRefreshing()
    {
        var legacy = Legacy();
        var current = Current();
        Assert.True(Valid(legacy));
        Assert.True(Valid(current));
        Assert.True(JsonNode.DeepEquals(legacy, FrameworkProvenance.Read(legacy)));
        var read = FrameworkProvenance.Read(current);
        read.Remove("frameworks");
        read["manifestVersion"] = 2;
        Assert.True(JsonNode.DeepEquals(legacy, read));
        var comparison = JsonNode.Parse("""{"files":[],"head_commit":{"sha":"abcdef"}}""")!;
        Assert.True(JsonNode.DeepEquals(DotnetSkillsDrift.Analyze(legacy, comparison), DotnetSkillsDrift.Analyze(current, comparison)));
        Assert.Equal("none", DotnetSkillsDrift.Analyze(current, comparison)["automaticAction"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Current(), current));
    }

    [Fact]
    public void MigratedInventoryUsesHistoricalPinAndExplicitUnknowns()
    {
        var manifest = FrameworkProvenance.Read(Current());
        Assert.Equal(3, manifest["manifestVersion"]!.GetValue<int>());
        var entry = Assert.Single(manifest["frameworks"]!.AsArray())!;
        Assert.Equal("dotnet", entry["frameworkId"]!.GetValue<string>());
        Assert.Equal(".NET", Assert.Single(entry["aliases"]!.AsArray())!.GetValue<string>());
        Assert.Equal(manifest["snapshot"]!["url"]!.GetValue<string>(), entry["sourceUri"]!.GetValue<string>());
        Assert.Equal(manifest["snapshot"]!["commit"]!.GetValue<string>(), entry["sourceRevision"]!.GetValue<string>());
        foreach (var field in new[] { "supportedMajorVersions", "sourceHash", "capturedAt" })
            Assert.Equal("unknown", entry[field]!.GetValue<string>());
        foreach (var skill in entry["consumingSkillIds"]!.AsArray())
            Assert.True(File.Exists(Path.Combine(Root, "plugins/sdeveng/skills", skill!.GetValue<string>(), "SKILL.md")));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("microsoft.extensions")]
    [InlineData("system.commandline")]
    [InlineData("avalonia")]
    [InlineData("terminal.gui")]
    public void FrameworkVariantsSupportKnownVersionsAndHashes(string id)
    {
        var manifest = Current();
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tests/SdevEng.Tests/Fixtures/FrameworkProvenance/framework.json")))!;
        manifest["frameworks"]![0] = entry;
        entry["frameworkId"] = id;
        entry["supportedMajorVersions"] = new JsonArray(10, 11);
        entry["sourceHash"] = "sha256:" + new string('a', 64);
        Assert.True(Valid(manifest));
        Assert.True(JsonNode.DeepEquals(manifest, FrameworkProvenance.Read(manifest)));
    }

    [Theory]
    [InlineData("frameworkId", "future")]
    [InlineData("sourceHash", "refreshed")]
    [InlineData("sourceUri", "file:///tmp/source")]
    [InlineData("sourceRevision", "")]
    [InlineData("capturedAt", "2026-09-22")]
    [InlineData("supportedMajorVersions", "latest")]
    public void InvalidEntryFailsSchemaAndOwningReader(string field, string value)
    {
        var manifest = Current();
        manifest["frameworks"]![0]![field] = value;
        Assert.False(Valid(manifest));
        Assert.Throws<FormatException>(() => FrameworkProvenance.Read(manifest));
    }

    [Fact]
    public void VersionsAndAliasesFailClosedAtTheConsumer()
    {
        var manifest = Current();
        manifest["manifestVersion"] = 4;
        Assert.False(Valid(manifest));
        Assert.Throws<FormatException>(() => FrameworkProvenance.Read(manifest));
        manifest = Current();
        manifest["frameworks"]!.AsArray().Add(manifest["frameworks"]![0]!.DeepClone());
        Assert.Throws<FormatException>(() => FrameworkProvenance.Read(manifest));
        manifest = Current();
        manifest["frameworks"]![0]!["aliases"] = new JsonArray("Avalonia");
        Assert.Throws<FormatException>(() => DotnetSkillsDrift.Analyze(manifest, JsonNode.Parse("""{"files":[]}""")!));
        manifest = Current();
        manifest["frameworks"]![0]!["supportedMajorVersions"] = new JsonArray(10, 10);
        Assert.False(Valid(manifest));
        Assert.Throws<FormatException>(() => FrameworkProvenance.Read(manifest));
    }
}
