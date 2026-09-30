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

    private static StartWorkCoordinator Registered(IssueClient client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(client);
        return services.BuildServiceProvider().GetRequiredService<StartWorkCoordinator>();
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
