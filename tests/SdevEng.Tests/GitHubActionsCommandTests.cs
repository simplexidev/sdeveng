using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class GitHubActionsCommandTests
{
    [Fact]
    public async Task RuntimeUsesTypedActionsReadsForRunListAndDetail()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/example/project.git");
        var client = new StubReadClient(
            """{"workflow_runs":[{"id":81,"name":"CI","display_title":"Build","status":"completed","conclusion":"failure","event":"push","head_branch":"main","head_sha":"abc","html_url":"https://github.com/example/project/actions/runs/81"}]}""",
            """{"id":81,"name":"CI","display_title":"Build","status":"completed","conclusion":"failure","event":"push","head_branch":"main","head_sha":"abc","html_url":"https://github.com/example/project/actions/runs/81"}""",
            """{"jobs":[{"id":91,"name":"build","status":"completed","conclusion":"failure","steps":[{"name":"compile","number":2,"conclusion":"failure"}]},{"id":92,"name":"deploy","status":"completed","conclusion":"cancelled","steps":[]}]}""");
        using var provider = CreateProvider(client);
        var runtime = provider.GetRequiredService<AgentTool.AgentToolRuntime>();
        var settings = new Settings(new(), new(), new(), new());

        var list = await runtime.Execute(Cli.Parse(["github", "actions", "--json"]), AgentTool.FindToolkit(), repo.Root, settings);
        var detail = await runtime.Execute(Cli.Parse(["github", "actions", "--run-id", "81", "--json"]), AgentTool.FindToolkit(), repo.Root, settings);

        var listJson = System.Text.Json.JsonSerializer.SerializeToNode(list.Data)!;
        var detailJson = System.Text.Json.JsonSerializer.SerializeToNode(detail.Data)!;
        Assert.Equal("github-actions-summary", listJson["kind"]!.GetValue<string>());
        Assert.Equal("runs", listJson["mode"]!.GetValue<string>());
        Assert.Equal("run", detailJson["mode"]!.GetValue<string>());
        Assert.Equal(1, detailJson["failedJobs"]!.GetValue<int>());
        Assert.Equal(1, detailJson["cancelledJobs"]!.GetValue<int>());
        Assert.Equal("compile", detailJson["jobs"]![0]!["failedSteps"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(3, client.Endpoints.Count);
        Assert.All(client.Endpoints, endpoint => Assert.Contains("api.github.com/repos/example/project/actions/", endpoint.Host + endpoint.AbsolutePath));
    }

    [Fact]
    public async Task FailedLogsRequireExplicitRunIdAndUseTypedReader()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/example/project.git");
        var client = new StubReadClient("""{"jobs":[{"id":91,"name":"build","conclusion":"failure"}]}""", "compile failed");
        using var provider = CreateProvider(client);
        var runtime = provider.GetRequiredService<AgentTool.AgentToolRuntime>();
        var settings = new Settings(new(), new(), new(), new());

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Execute(Cli.Parse(["github", "actions", "--failed-logs", "--json"]), AgentTool.FindToolkit(), repo.Root, settings));
        var result = await runtime.Execute(Cli.Parse(["github", "actions", "--run-id", "81", "--failed-logs", "--json"]), AgentTool.FindToolkit(), repo.Root, settings);

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, client.Endpoints.Count);
        Assert.Contains("actions/jobs/91/logs", client.Endpoints[1].AbsolutePath);
    }

    static ServiceProvider CreateProvider(IGitHubReadClient client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton(client);
        services.AddSingleton<IGitHubReadClient>(client);
        return services.BuildServiceProvider();
    }

    sealed class StubReadClient(params string[] responses) : IGitHubReadClient
    {
        int index;
        public List<Uri> Endpoints { get; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            var content = responses[index++];
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
