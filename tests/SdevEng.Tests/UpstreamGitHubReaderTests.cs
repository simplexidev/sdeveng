using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public sealed class UpstreamGitHubReaderTests
{
    [Fact]
    public async Task DotnetSkillsCommandsUseTypedReaderAndPreserveDriftClassification()
    {
        using var repo = new TemporaryGitRepository();
        var toolkit = AgentTool.FindToolkit();
        var provenancePath = Path.Combine(toolkit, "upstream/dotnet-skills.json");
        var original = File.ReadAllText(provenancePath);
        Assert.Equal(3, FrameworkProvenance.Read(JsonNode.Parse(original)!)["manifestVersion"]!.GetValue<int>());
        var reader = new GitHubCommitReader(new FakeReadClient(
            """{"sha":"different"}""",
            """{"head_commit":{"sha":"newhead"},"files":[{"filename":"plugins/dotnet-test/skills/run-tests/SKILL.md","status":"modified"}]}"""));
        var module = new AgentTool.UpstreamCommandModule(reader);
        var settings = new Settings(new(), new(), new(), new());

        var status = await module.Execute(Cli.Parse(["upstream", "dotnet-skills", "status"]), toolkit, repo.Root, settings, CancellationToken.None);
        Assert.Equal("review-update", JsonSerializer(status.Data)["status"]!.GetValue<string>());
        var diff = await module.Execute(Cli.Parse(["upstream", "dotnet-skills", "check"]), toolkit, repo.Root, settings, CancellationToken.None);
        Assert.Equal("review-required", diff.Status);
        var analysis = JsonSerializer(diff.Data)["analysis"]!;
        Assert.Equal("none", analysis["automaticAction"]!.GetValue<string>());
        Assert.Equal(JsonNode.Parse(original)!["snapshot"]!["commit"]!.GetValue<string>(), analysis["pinned"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(provenancePath));
    }

    [Fact]
    public async Task UpdateUsesTypedReaderAndDryRunMakesNoRequest()
    {
        using var repo = new TemporaryGitRepository();
        var client = new FakeReadClient("""{"sha":"abc123"}""");
        var module = new AgentTool.UpstreamCommandModule(new GitHubCommitReader(client));
        var settings = new Settings(new(), new(), new(), new());
        var dryRun = await module.Execute(Cli.Parse(["upstream", "update", "--dry-run"]), AgentTool.FindToolkit(), repo.Root, settings, CancellationToken.None);
        Assert.Empty(client.Endpoints);
        Assert.Contains("gh api", System.Text.Json.JsonSerializer.Serialize(dryRun.Data));
        var result = await module.Execute(Cli.Parse(["upstream", "update"]), AgentTool.FindToolkit(), repo.Root, settings, CancellationToken.None);
        Assert.NotEmpty(client.Endpoints);
        Assert.Equal("review-update", System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!["rows"]![0]!["status"]!.GetValue<string>());
    }

    static JsonObject JsonSerializer(object? value) => System.Text.Json.JsonSerializer.SerializeToNode(value, AgentTool.Json)!.AsObject();

    [Theory]
    [InlineData("status", "dotnet-skills-status")]
    [InlineData("diff", "dotnet-skills-diff")]
    [InlineData("check", "dotnet-skills-diff")]
    public async Task CompatibleDriftConsumerPreservesUnavailableResult(string operation, string kind)
    {
        using var repo = new TemporaryGitRepository();
        var toolkit = AgentTool.FindToolkit();
        var path = Path.Combine(toolkit, "upstream/dotnet-skills.json");
        var original = File.ReadAllText(path);
        var module = new AgentTool.UpstreamCommandModule(new GitHubCommitReader(new UnavailableReadClient()));
        var result = await module.Execute(Cli.Parse(["upstream", "dotnet-skills", operation]),
            toolkit, repo.Root, new Settings(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(1, result.ExitCode);
        var data = JsonSerializer(result.Data);
        Assert.Equal(kind, data["kind"]!.GetValue<string>());
        Assert.Equal("dotnet/skills", data["repository"]!.GetValue<string>());
        Assert.Equal("4ed5f7c121da8dd31af31a35cef05070948c6556", data["pinned"]!.GetValue<string>());
        if (operation == "status") Assert.Null(data["latest"]);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool")));
    }

    sealed class UnavailableReadClient : IGitHubReadClient
    {
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    sealed class FakeReadClient(params string[] bodies) : IGitHubReadClient
    {
        int _index;
        public List<Uri> Endpoints { get; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            var body = bodies[Math.Min(_index++, bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
