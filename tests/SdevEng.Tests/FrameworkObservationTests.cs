using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class FrameworkObservationTests
{
    static string Root => AgentTool.FindToolkit();
    static JsonObject Manifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "upstream/dotnet-skills.json")))!.AsObject();
    static JsonObject Observation(JsonObject manifest) => new()
    {
        ["schemaVersion"] = 1,
        ["observations"] = new JsonArray(new JsonObject
        {
            ["frameworkId"] = "dotnet",
            ["sourceUri"] = "https://github.com/dotnet/skills",
            ["observedRevision"] = manifest["frameworks"]![0]!["sourceRevision"]!.DeepClone(),
            ["observedHash"] = "sha256:" + new string('a', 64),
            ["observedMajorVersion"] = 10,
            ["capturedAt"] = "2026-10-08T12:00:00Z",
            ["provenance"] = "synthetic local snapshot",
            ["unavailableReason"] = null
        })
    };

    public static IEnumerable<object[]> EvaluationCases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "tests/SdevEng.Tests/Fixtures/FrameworkProvenance/drift-evaluation.json")))!["cases"]!.AsArray().Select(c => new object[] { c![0]!.GetValue<string>(), c[1]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(EvaluationCases))]
    public async Task NormalDispatchProducesValidatedDeterministicPlan(string scenario, string expected)
    {
        using var repo = new TemporaryGitRepository();
        Directory.CreateDirectory(Path.Combine(repo.Root, "upstream"));
        var manifest = Manifest();
        manifest["frameworks"]![0]!["sourceHash"] = "sha256:" + new string('a', 64);
        var input = Observation(manifest);
        var observation = input["observations"]![0]!;
        if (scenario == "revision") observation["observedRevision"] = "different";
        if (scenario == "hash") observation["observedHash"] = "sha256:" + new string('b', 64);
        if (scenario == "unsupported") observation["observedMajorVersion"] = 11;
        if (scenario == "unknown") manifest["frameworks"]![0]!["sourceHash"] = "unknown";
        if (scenario == "unavailable") { observation["observedHash"] = null; observation["unavailableReason"] = "synthetic capture unavailable"; }
        var path = Path.Combine(repo.Root, "upstream/dotnet-skills.json");
        var original = manifest.ToJsonString(); File.WriteAllText(path, original);
        var observations = Path.Combine(repo.Root, "observations.json"); File.WriteAllText(observations, input.ToJsonString());
        Assert.True(JsonSchema.FromFile(Path.Combine(Root, "schemas/framework-observations.schema.json")).Evaluate(input, new() { RequireFormatValidation = true }).IsValid);
        foreach (var operation in new[] { "drift", "refresh" })
        {
            string[] args = scenario == "absent" ? ["upstream", "dotnet-skills", operation] : ["upstream", "dotnet-skills", operation, "--observations-file", observations];
            var command = Cli.Parse(args);
            command.ValidateCommand(command.Command);
            var result = await CommandTestRuntime.Execute(command, repo.Root, repo.Root, new Settings(new(), new(), new(), new()));
            Assert.Equal(0, result.ExitCode);
            var data = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            var options = new EvaluationOptions { RequireFormatValidation = true };
            var inputSchemaPath = Path.Combine(Root, "schemas/framework-observations.schema.json");
            options.SchemaRegistry.Register(new Uri(inputSchemaPath), JsonSchema.FromFile(inputSchemaPath));
            Assert.True(JsonSchema.FromFile(Path.Combine(Root, "schemas/framework-drift-plan.schema.json")).Evaluate(data, options).IsValid);
            Assert.Equal(expected, data["entries"]![0]!["classification"]!.GetValue<string>());
            Assert.Equal(operation == "refresh" ? "plan" : "facts", data["mode"]!.GetValue<string>());
            Assert.Equal("none", data["automaticAction"]!.GetValue<string>());
            Assert.All(data["entries"]!.AsArray().Skip(1), e => Assert.Equal("unknown", e!["classification"]!.GetValue<string>()));
            var repeated = await CommandTestRuntime.Execute(Cli.Parse(args), repo.Root, repo.Root, new Settings(new(), new(), new(), new()));
            Assert.True(JsonNode.DeepEquals(data, JsonSerializer.SerializeToNode(repeated.Data, AgentTool.Json)));
            Assert.Equal(original, File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task LegacySnapshotRemainsUnknownAndLegacyOperationsRejectObservations()
    {
        using var repo = new TemporaryGitRepository();
        Directory.CreateDirectory(Path.Combine(repo.Root, "upstream"));
        var legacy = File.ReadAllText(Path.Combine(Root, "tests/SdevEng.Tests/Fixtures/FrameworkProvenance/legacy-v2.json"));
        File.WriteAllText(Path.Combine(repo.Root, "upstream/dotnet-skills.json"), legacy);
        var result = await CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", "refresh"]), repo.Root, repo.Root, new Settings(new(), new(), new(), new()));
        var data = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        Assert.All(data["entries"]!.AsArray(), e => Assert.Equal("unknown", e!["classification"]!.GetValue<string>()));
        Assert.Equal(JsonNode.Parse(legacy)!["snapshot"]!["commit"]!.GetValue<string>(), data["entries"]![0]!["currentRevision"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", "check", "--observations-file", "unused.json"]), repo.Root, repo.Root, new Settings(new(), new(), new(), new())));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("source")]
    [InlineData("id")]
    [InlineData("hash")]
    [InlineData("major")]
    [InlineData("time")]
    [InlineData("reason")]
    [InlineData("oversize")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("unknown-marker")]
    [InlineData("extra")]
    public async Task InvalidSnapshotsRejectedThroughDispatch(string scenario)
    {
        using var repo = new TemporaryGitRepository();
        Directory.CreateDirectory(Path.Combine(repo.Root, "upstream"));
        var manifest = Manifest(); var input = Observation(manifest); var item = input["observations"]![0]!;
        switch (scenario)
        {
            case "duplicate": input["observations"]!.AsArray().Add(item.DeepClone()); break;
            case "source": item["sourceUri"] = "https://example.com/other"; break;
            case "id": item["frameworkId"] = "unregistered"; break;
            case "hash": item["observedHash"] = "invalid"; break;
            case "major": item["observedMajorVersion"] = 0; break;
            case "time": item["capturedAt"] = "unknown"; break;
            case "reason": item["observedRevision"] = null; break;
            case "version": input["schemaVersion"] = 2; break;
            case "revision": item["observedRevision"] = "bad revision"; break;
            case "unknown-marker": item["observedRevision"] = "unknown"; break;
            case "extra": item["unrecognized"] = true; break;
            case "oversize": item["provenance"] = new string('x', 17000); break;
        }
        File.WriteAllText(Path.Combine(repo.Root, "upstream/dotnet-skills.json"), manifest.ToJsonString());
        var observations = Path.Combine(repo.Root, "observations.json"); File.WriteAllText(observations, input.ToJsonString());
        await Assert.ThrowsAsync<FormatException>(() => CommandTestRuntime.Execute(Cli.Parse(["upstream", "dotnet-skills", "refresh", "--observations-file", observations]), repo.Root, repo.Root, new Settings(new(), new(), new(), new())));
    }
}
