using System.Net;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class StartWorkCoordinatorTests
{
    [Fact]
    public async Task ExactBaseRecordsOriginIssueAndStartingThroughRegisteredCoordinator()
    {
        using var repo = NewRepository();
        var request = NewRequest(repo);
        var client = new IssueClient();
        var coordinator = Registered(client);

        var result = await coordinator.StartAsync(request);

        Assert.Equal("STARTING", result.State);
        Assert.Equal("owner/project", result.Repository);
        Assert.Equal(request.ExpectedBaseSha, result.BaseSha);
        Assert.Equal("main", (await Git.State(repo.Root)).Branch);
        Assert.Empty(repo.Run("branch", "--list", request.BranchName).Trim());
        Assert.Equal(new Uri("https://api.github.com/repos/owner/project/issues/42"), client.Endpoint);
        var events = LocalRunEventStore.Read(Store(repo), request.ProductRunId);
        Assert.Equal(new[] { "created", "external-identifier-recorded", "external-identifier-recorded", "STARTING" },
            events.Select(item => item.GetProperty("eventType").GetString() == "state-transition"
                ? item.GetProperty("toState").GetString() : item.GetProperty("eventType").GetString()));
        Assert.Equal(new[] { "repository", "issue" }, events.Skip(1).Take(2).Select(item => item.GetProperty("identifierType").GetString()));
        Assert.Equal(new[] { "owner/project", "42" }, events.Skip(1).Take(2).Select(item => item.GetProperty("identifier").GetString()));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
        Assert.All(events, item => Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid));
        Assert.Equal("STARTING", System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Status(Store(repo), request.ProductRunId), AgentTool.Json)!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("unknown", false)]
    [InlineData("denied", false)]
    public async Task ContinueCreatesOwnedExactBranchAndPushesOnlyWhenAllowed(string capability, bool pushed)
    {
        using var repo = NewRepository();
        var bare = Path.Combine(Path.GetTempPath(), "sdeveng-start-bare-" + Guid.NewGuid().ToString("N"));
        await Processes.Run("git", ["init", "--bare", bare], repo.Root);
        try
        {
            repo.Run("remote", "set-url", "--push", "origin", bare);
            var request = NewRequest(repo);
            var coordinator = Registered(new IssueClient(), capability);
            await coordinator.StartAsync(request);
            if (pushed) await coordinator.ContinueAsync(request);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ContinueAsync(request));
            Assert.Equal(request.ExpectedBaseSha, repo.Run("rev-parse", "refs/heads/" + request.BranchName).Trim());
            Assert.Equal(GitOwnershipMarkers.BranchConfigValue, repo.Run("config", "--get", "branch." + request.BranchName + "." + GitOwnershipMarkers.BranchConfigKey).Trim());
            var remote = await Processes.Run("git", ["--git-dir", bare, "for-each-ref", "--format=%(refname):%(objectname)", "refs/heads"], repo.Root);
            Assert.Equal(pushed ? $"refs/heads/{request.BranchName}:{request.ExpectedBaseSha}\n" : "", remote.Output);
            var events = LocalRunEventStore.Read(Store(repo), request.ProductRunId);
            Assert.Equal(pushed ? 1 : 0, events.Count(item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == "branch-pushed"));
            Assert.Contains(events, item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == "branch-created");
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.All(events, item => Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid));
            if (pushed)
            {
                var count = events.Count;
                await coordinator.ContinueAsync(request);
                Assert.Equal(count, LocalRunEventStore.Read(Store(repo), request.ProductRunId).Count);
            }
        }
        finally { Directory.Delete(bare, true); }
    }

    [Fact]
    public async Task FailedPushKeepsOwnedBranchForResume()
    {
        using var repo = NewRepository();
        repo.Run("remote", "set-url", "--push", "origin", Path.Combine(repo.Root, "missing-bare-remote"));
        var request = NewRequest(repo);
        var coordinator = Registered(new IssueClient(), "allowed");
        await coordinator.StartAsync(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ContinueAsync(request));
        Assert.Equal(request.ExpectedBaseSha, repo.Run("rev-parse", "refs/heads/" + request.BranchName).Trim());
        Assert.DoesNotContain(LocalRunEventStore.Read(Store(repo), request.ProductRunId), item =>
            item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == "branch-pushed");
        var bare = Path.Combine(Path.GetTempPath(), "sdeveng-resume-bare-" + Guid.NewGuid().ToString("N"));
        await Processes.Run("git", ["init", "--bare", bare], repo.Root);
        try
        {
            repo.Run("remote", "set-url", "--push", "origin", bare);
            await coordinator.ContinueAsync(request);
            var events = LocalRunEventStore.Read(Store(repo), request.ProductRunId);
            Assert.Single(events, item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == "branch-created");
            Assert.Single(events, item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == "branch-pushed");
            Assert.DoesNotContain(events, item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("identifierType").GetString() == "pull-request");
        }
        finally { Directory.Delete(bare, true); }
    }

    [Fact]
    public async Task StaleBaseLeavesRunAndRepositoryUntouched()
    {
        using var repo = NewRepository();
        var request = NewRequest(repo) with { ExpectedBaseSha = new string('0', 40) };
        var client = new IssueClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Registered(client).StartAsync(request));

        Assert.Null(client.Endpoint);
        AssertOnlyCreated(repo, request);
    }

    [Theory]
    [InlineData("run-id")]
    [InlineData("issue-number")]
    [InlineData("base-ref")]
    [InlineData("origin")]
    public async Task InvalidTargetCannotRecordStarting(string failure)
    {
        using var repo = NewRepository();
        var request = NewRequest(repo);
        var originalRequest = request;
        if (failure == "run-id") request = request with { ProductRunId = Guid.Empty };
        if (failure == "issue-number") request = request with { SourceIssueNumber = 0 };
        if (failure == "base-ref") request = request with { BaseRef = "bad..ref" };
        if (failure == "origin") repo.Run("remote", "set-url", "origin", "https://example.com/owner/project.git");

        await Assert.ThrowsAnyAsync<Exception>(() => Registered(new IssueClient()).StartAsync(request));

        AssertOnlyCreated(repo, originalRequest);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("busy")]
    [InlineData("branch")]
    [InlineData("issue-repository")]
    public async Task InvalidPreconditionsLeaveOnlyCreatedEvent(string failure)
    {
        using var repo = NewRepository();
        var request = NewRequest(repo);
        var client = new IssueClient();
        if (failure == "dirty") repo.Write("dirty.txt", "untracked");
        if (failure == "busy") File.WriteAllText(Path.GetFullPath(repo.Run("rev-parse", "--git-path", "MERGE_HEAD").Trim(), repo.Root), request.ExpectedBaseSha);
        if (failure == "branch") request = request with { BranchName = "bad..branch" };
        if (failure == "issue-repository") client.IssueUrl = "https://github.com/other/project/issues/42";

        await Assert.ThrowsAnyAsync<Exception>(() => Registered(client).StartAsync(request));

        AssertOnlyCreated(repo, request);
    }

    private static TemporaryGitRepository NewRepository()
    {
        var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/owner/project.git");
        return repo;
    }

    private static StartWorkRequest NewRequest(TemporaryGitRepository repo)
    {
        var request = new StartWorkRequest(Guid.NewGuid(), repo.Root, 42, "main", repo.Run("rev-parse", "main").Trim(), "factory/issue-42");
        LocalRunEventStore.AppendTransition(Store(repo), request.ProductRunId, null, "created");
        return request;
    }

    private static string Store(TemporaryGitRepository repo) => Path.Combine(repo.Root, ".sdeveng", "runs");

    private static StartWorkCoordinator Registered(IssueClient client, string capability = "unknown")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(client);
        services.AddSingleton<AgentTool.IGitHubAuthorizationProcess>(new CapabilityProcess(capability));
        return services.BuildServiceProvider().GetRequiredService<StartWorkCoordinator>();
    }

    private sealed class CapabilityProcess(string capability) : AgentTool.IGitHubAuthorizationProcess
    {
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd)
        {
            var args = arguments.ToArray();
            var result = args.SequenceEqual(["remote", "get-url", "--all", "origin"]) ? new ProcessResult(0, "https://github.com/owner/project.git")
                : args.SequenceEqual(["auth", "status"]) ? new ProcessResult(0, "Token scopes: repo")
                : args.SequenceEqual(["rev-parse", "--short=12", "HEAD"]) ? new ProcessResult(0, "abcdef123456")
                : args.Contains("--dry-run") ? capability switch
                {
                    "allowed" => new ProcessResult(0, ""),
                    "denied" => new ProcessResult(1, "remote: error: protected branch"),
                    _ => new ProcessResult(1, "timeout")
                }
                : new ProcessResult(0, "{}");
            return Task.FromResult(result);
        }
    }

    private static void AssertOnlyCreated(TemporaryGitRepository repo, StartWorkRequest request)
    {
        var events = LocalRunEventStore.Read(Store(repo), request.ProductRunId);
        Assert.Single(events);
        Assert.Equal("created", events[0].GetProperty("toState").GetString());
        Assert.Empty(repo.Run("branch", "--list", request.BranchName).Trim());
        Assert.Equal("main", repo.Run("branch", "--show-current").Trim());
    }

    private sealed class IssueClient : IGitHubReadClient
    {
        public Uri? Endpoint { get; private set; }
        public string IssueUrl { get; set; } = "https://github.com/owner/project/issues/42";
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"number":42,"title":"Issue","state":"open","html_url":"{{IssueUrl}}"}""")
            });
        }
    }
}
