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

    [Theory]
    [InlineData(2, "legacy-observed-plan.json")]
    [InlineData(3, "current-observed-plan.json")]
    public async Task FixedObservationFixturesPreserveAuthorityThroughCurrentConsumers(int version, string expectedFile)
    {
        using var repo = new TemporaryGitRepository();
        var fixtures = Path.Combine(Root, "tests/SdevEng.Tests/Fixtures/FrameworkProvenance");
        var manifest = version == 2 ? Legacy() : Current();
        Assert.True(Valid(manifest));
        var original = manifest.ToJsonString();
        Directory.CreateDirectory(Path.Combine(repo.Root, "upstream"));
        var path = Path.Combine(repo.Root, "upstream/dotnet-skills.json");
        File.WriteAllText(path, original);
        var observations = Path.Combine(fixtures, "migration-observations.json");
        var input = JsonNode.Parse(File.ReadAllText(observations))!;
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, expectedFile)))!;
        var inputSchemaPath = Path.Combine(Root, "schemas/framework-observations.schema.json");
        var inputSchema = JsonSchema.FromFile(inputSchemaPath);
        Assert.True(inputSchema.Evaluate(input, new() { RequireFormatValidation = true }).IsValid);
        var options = new EvaluationOptions { RequireFormatValidation = true };
        options.SchemaRegistry.Register(new Uri(inputSchemaPath), inputSchema);
        var outputSchema = JsonSchema.FromFile(Path.Combine(Root, "schemas/framework-drift-plan.schema.json"));
        Assert.True(outputSchema.Evaluate(expected, options).IsValid);
        Assert.Single(FrameworkProvenance.ReadObservations(input.ToJsonString(), manifest).Observations);
        var settings = new Settings(new(), new(), new(), new());
        foreach (var operation in new[] { "drift", "refresh" })
        {
            var result = await CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", operation,
                "--observations-file", observations]), repo.Root, repo.Root, settings);
            Assert.Equal("ok", result.Status);
            Assert.Equal(0, result.ExitCode);
            var actual = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            Assert.True(outputSchema.Evaluate(actual, options).IsValid);
            var outcome = expected.DeepClone();
            outcome["mode"] = operation == "refresh" ? "plan" : "facts";
            Assert.True(JsonNode.DeepEquals(outcome, actual), actual.ToJsonString());
        }
        var status = await CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", "status", "--dry-run"]),
            repo.Root, repo.Root, settings);
        Assert.Equal(0, status.ExitCode);
        var statusData = System.Text.Json.JsonSerializer.SerializeToNode(status.Data, AgentTool.Json)!;
        Assert.Equal(expected["entries"]![0]!["currentRevision"]!.GetValue<string>(), statusData["pinned"]!.GetValue<string>());
        Assert.Equal("dotnet/skills", statusData["repository"]!.GetValue<string>());
        var analysis = DotnetSkillsDrift.Analyze(manifest, JsonNode.Parse("""{"files":[],"head_commit":{"sha":"synthetic-head"}}""")!);
        Assert.Equal("no-change", analysis["classification"]!.GetValue<string>());
        Assert.Equal(expected["entries"]![0]!["currentRevision"]!.GetValue<string>(), analysis["pinned"]!.GetValue<string>());
        Assert.Equal("none", analysis["automaticAction"]!.GetValue<string>());
        Assert.Equal(original, manifest.ToJsonString());
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool")));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "artifacts")));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UpstreamStatusPreservesValidatedInventoryThroughNormalDispatch(int version)
    {
        using var repo = new TemporaryGitRepository();
        var upstream = Path.Combine(repo.Root, "upstream");
        Directory.CreateDirectory(upstream);
        var manifest = version == 2 ? Legacy() : Current();
        var original = manifest.ToJsonString();
        var path = Path.Combine(upstream, "dotnet-skills.json");
        File.WriteAllText(path, original);
        File.WriteAllText(Path.Combine(upstream, "tools.json"), "{\"tools\":[]}");
        File.WriteAllText(Path.Combine(upstream, "versions.json"), "{\"repositories\":[]}");

        var result = await CommandTestRuntime.Execute(Cli.Parse(["upstream", "status"]),
            repo.Root, repo.Root, new Settings(new(), new(), new(), new()));
        Assert.Equal("ok", result.Status);
        Assert.Equal(0, result.ExitCode);
        var data = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        Assert.Equal(new[] { "plugins", "tools", "versions" }, data.AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(manifest, data["plugins"]));
        Assert.True(Valid(data["plugins"]!));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"tools\":[]}"), data["tools"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"repositories\":[]}"), data["versions"]));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool")));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("alias")]
    [InlineData("empty")]
    public async Task DirectStatusConsumerRejectsInvalidInventoryThroughNormalDispatch(string scenario)
    {
        using var repo = new TemporaryGitRepository();
        Directory.CreateDirectory(Path.Combine(repo.Root, "upstream"));
        var manifest = Current();
        if (scenario == "version") manifest["manifestVersion"] = 4;
        if (scenario == "hash") manifest["frameworks"]![0]!["sourceHash"] = "bad";
        if (scenario == "alias") manifest["frameworks"]![0]!["aliases"] = new JsonArray("avalonia");
        var original = scenario == "empty" ? "null" : manifest.ToJsonString();
        var path = Path.Combine(repo.Root, "upstream/dotnet-skills.json");
        File.WriteAllText(path, original);
        await Assert.ThrowsAsync<FormatException>(() => CommandTestRuntime.Execute(Cli.Parse(["upstream", "status"]),
            repo.Root, repo.Root, new Settings(new(), new(), new(), new())));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool")));
    }

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
        foreach (var entry in read["frameworks"]!.AsArray())
        {
            entry!.AsObject().Remove("releaseUri");
            entry.AsObject().Remove("documentationUri");
        }
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
        var entry = Assert.Single(manifest["frameworks"]!.AsArray(), x => x!["frameworkId"]!.GetValue<string>() == "dotnet")!;
        Assert.Equal("dotnet", entry["frameworkId"]!.GetValue<string>());
        Assert.Equal(".NET", Assert.Single(entry["aliases"]!.AsArray())!.GetValue<string>());
        Assert.Equal(manifest["snapshot"]!["url"]!.GetValue<string>(), entry["sourceUri"]!.GetValue<string>());
        Assert.Equal(manifest["snapshot"]!["commit"]!.GetValue<string>(), entry["sourceRevision"]!.GetValue<string>());
        Assert.Equal(new[] { 10 }, entry["supportedMajorVersions"]!.AsArray().Select(v => v!.GetValue<int>()));
        foreach (var field in new[] { "sourceHash", "capturedAt" })
            Assert.Equal("unknown", entry[field]!.GetValue<string>());
        foreach (var skill in entry["consumingSkillIds"]!.AsArray())
            Assert.True(File.Exists(Path.Combine(Root, "plugins/sdeveng/skills", skill!.GetValue<string>(), "SKILL.md")));
    }

    [Fact]
    public void InventoryRecordsSupportedMajorBoundariesWithoutChangingHistoricalPins()
    {
        var manifest = FrameworkProvenance.Read(Current());
        Assert.Equal(FrameworkProvenance.FrameworkIds.Order(), manifest["frameworks"]!.AsArray()
            .Select(entry => entry!["frameworkId"]!.GetValue<string>()).Order());
        var supported = new Dictionary<string, int[]>
        {
            ["dotnet"] = [10],
            ["microsoft.extensions"] = [10],
            ["avalonia"] = [11]
        };
        foreach (var entry in manifest["frameworks"]!.AsArray())
        {
            var value = entry!.AsObject();
            var id = value["frameworkId"]!.GetValue<string>();
            if (supported.TryGetValue(id, out var majors))
                Assert.Equal(majors, value["supportedMajorVersions"]!.AsArray().Select(x => x!.GetValue<int>()));
            else
                Assert.Equal("unknown", value["supportedMajorVersions"]!.GetValue<string>());
            if (id == "avalonia") Assert.Equal("d6edb46ce04f983892a61d3abf906014d3f5ec8d", value["sourceRevision"]!.GetValue<string>());
            if (id != "dotnet")
            {
                if (id != "avalonia") Assert.Equal("unknown", value["sourceRevision"]!.GetValue<string>());
                Assert.Equal("unknown", value["sourceHash"]!.GetValue<string>());
                Assert.Equal("unknown", value["capturedAt"]!.GetValue<string>());
            }
            Assert.NotEmpty(entry["consumingSkillIds"]!.AsArray());
        }
        Assert.Equal("4ed5f7c121da8dd31af31a35cef05070948c6556", manifest["frameworks"]![0]!["sourceRevision"]!.GetValue<string>());
        Assert.True(Valid(manifest));
    }

    [Fact]
    public void InventoryRegistersAuthoritativeReleaseAndDocumentationSources()
    {
        var expected = new Dictionary<string, (string Release, string Docs)>
        {
            ["dotnet"] = ("https://github.com/dotnet/skills/releases", "https://learn.microsoft.com/dotnet/"),
            ["microsoft.extensions"] = ("https://github.com/dotnet/extensions/releases", "https://learn.microsoft.com/dotnet/core/extensions/"),
            ["system.commandline"] = ("https://github.com/dotnet/command-line-api/releases", "https://learn.microsoft.com/dotnet/standard/commandline/"),
            ["avalonia"] = ("https://github.com/AvaloniaUI/Avalonia/releases", "https://docs.avaloniaui.net/"),
            ["terminal.gui"] = ("https://github.com/gui-cs/Terminal.Gui/releases", "https://gui-cs.github.io/Terminal.GuiV2Docs/")
        };
        foreach (var entry in FrameworkProvenance.Read(Current())["frameworks"]!.AsArray())
        {
            var value = entry!.AsObject();
            var sources = expected[value["frameworkId"]!.GetValue<string>()];
            Assert.Equal(sources.Release, value["releaseUri"]!.GetValue<string>());
            Assert.Equal(sources.Docs, value["documentationUri"]!.GetValue<string>());
            Assert.StartsWith("https://", value["sourceUri"]!.GetValue<string>());
        }
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
        manifest["frameworks"] = new JsonArray(entry);
        entry["frameworkId"] = id;
        entry["supportedMajorVersions"] = new JsonArray(10);
        entry["sourceHash"] = "sha256:" + new string('a', 64);
        Assert.True(Valid(manifest));
        Assert.True(JsonNode.DeepEquals(manifest, FrameworkProvenance.Read(manifest)));
    }

    [Theory]
    [InlineData("frameworkId", "future")]
    [InlineData("sourceHash", "refreshed")]
    [InlineData("sourceUri", "file:///tmp/source")]
    [InlineData("releaseUri", "file:///tmp/releases")]
    [InlineData("documentationUri", "file:///tmp/docs")]
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
        manifest["frameworks"]![0]!["supportedMajorVersions"] = new JsonArray(0);
        Assert.False(Valid(manifest));
        Assert.Throws<FormatException>(() => FrameworkProvenance.Read(manifest));
    }
}
