using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class GitIssueStartTests
{
    [Theory]
    [InlineData("closed", false)]
    [InlineData("open", true)]
    public async Task IssueStartUsesRegisteredTypedReaderAndCreatesBranchOnlyForOpenIssue(string state, bool expectBranch)
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/owner/project.git");
        var readClient = new IssueResponseClient($"{{\"number\":42,\"title\":\"Issue\",\"state\":\"{state}\",\"html_url\":\"https://github.com/owner/project/issues/42\"}}");
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(readClient);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<AgentTool.AgentToolRuntime>();
        var command = Cli.Parse(["git", "issue-start", "--issue", "42", "--branch", "factory/issue-42"]);

        if (expectBranch)
        {
            var result = await runtime.Execute(command, "", repo.Root, new(new(), new(), new(), new()));
            Assert.Equal("ok", result.Status);
            Assert.Equal("factory/issue-42", (await Git.State(repo.Root)).Branch);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Execute(command, "", repo.Root, new(new(), new(), new(), new())));
            Assert.Empty(repo.Run("branch", "--list", "factory/issue-42").Trim());
        }

        Assert.Equal(new Uri("https://api.github.com/repos/owner/project/issues/42"), readClient.Endpoint);
    }

    [Fact]
    public async Task UnavailableIssueCreatesNoBranch()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/owner/project.git");
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(new IssueResponseClient(null));
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["git", "issue-start", "--issue", "42", "--branch", "factory/issue-42"]), "", repo.Root, new(new(), new(), new(), new())));
        Assert.Empty(repo.Run("branch", "--list", "factory/issue-42").Trim());
    }

    private sealed class IssueResponseClient(string? body) : IGitHubReadClient
    {
        public Uri? Endpoint { get; private set; }

        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            return body is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
