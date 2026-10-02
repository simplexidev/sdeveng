using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class CiObservationServiceTests
{
    [Fact]
    public async Task ObservesPersistedShaMapsStatesAndBoundsChecksThroughRegisteredService()
    {
        using var directory = new TemporaryDirectory();
        var runId = Guid.NewGuid();
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        LocalRunEventStore.AppendCommitIdentifier(directory.Path, runId, sha);
        var transport = new StubReadClient(""""
            {"check_runs":[
            {"id":1,"name":"queued","status":"queued","conclusion":null,"html_url":"https://github.com/o/r/runs/1"},
            {"id":2,"name":"success","status":"completed","conclusion":"success"},
            {"id":3,"name":"neutral","status":"completed","conclusion":"neutral"},
            {"id":4,"name":"failure","status":"completed","conclusion":"failure"},
            {"id":5,"name":"timeout","status":"completed","conclusion":"timed_out"},
            {"id":6,"name":"required","status":"completed","conclusion":"action_required"},
            {"id":7,"name":"cancelled","status":"completed","conclusion":"cancelled"},
            {"id":8,"name":"skipped","status":"completed","conclusion":"skipped"},
            {"id":9,"name":"future","status":"completed","conclusion":"future_value"}] }
            """", """
            {"workflow_runs":[
            {"id":11,"name":"CI","status":"completed","conclusion":"success","event":"push","head_branch":"main","head_sha":"0123456789abcdef0123456789abcdef01234567","html_url":"https://github.com/o/r/actions/runs/11"},
            {"id":12,"name":"CI","status":"completed","conclusion":"failure","event":"push","head_branch":"main","head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","html_url":"https://github.com/o/r/actions/runs/12"},
            {"id":13,"name":"CI","status":"in_progress","event":"push","head_branch":"main","head_sha":"0123456789abcdef0123456789abcdef01234567","html_url":"https://github.com/o/r/actions/runs/13"}]}
            """);
        var services = new ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(transport);
        using var provider = services.BuildServiceProvider();

        var observed = await provider.GetRequiredService<CiObservationService>().ObserveAsync(directory.Path, runId, "o", "r", limit: 8);

        Assert.Equal(sha, observed.CommitSha);
        Assert.Equal(new Uri($"https://api.github.com/repos/o/r/commits/{sha}/check-runs"), transport.Endpoints[0]);
        Assert.Equal($"https://api.github.com/repos/o/r/actions/runs?head_sha={sha}&per_page=8", transport.Endpoints[1].ToString());
        Assert.Equal(new[] { "pending", "success", "success", "failure", "failure", "failure", "cancelled", "skipped" }, observed.Checks.Select(check => check.State));
        Assert.Equal((1, "queued", "queued", (string?)null), (observed.Checks[0].Id, observed.Checks[0].Name, observed.Checks[0].ProviderStatus, observed.Checks[0].ProviderConclusion));
        Assert.Equal(new Uri("https://github.com/o/r/runs/1"), observed.Checks[0].DetailsUrl);
        Assert.Equal(new long[] { 11, 13 }, observed.WorkflowRuns.Select(run => run.Id));
        Assert.All(observed.WorkflowRuns, run => Assert.Equal(sha, run.Sha));
        var snapshot = Assert.Single(LocalRunEventStore.Read(directory.Path, runId), item => item.GetProperty("eventType").GetString() == "ci-check-snapshot");
        Assert.Equal(sha, snapshot.GetProperty("commitSha").GetString());
        Assert.Equal(new[] { "pending", "success", "success", "failure", "failure", "failure", "cancelled", "skipped" },
            snapshot.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("state").GetString()));
        Assert.Equal(new[] { "11", "13" }, snapshot.GetProperty("workflowRuns").EnumerateArray().Select(run => run.GetProperty("id").GetString()));
        Assert.Equal(2, transport.Endpoints.Count);
    }

    [Fact]
    public async Task RequiresExplicitFullShaWhenRunHasNoCommitAndPreservesUnknownConclusion()
    {
        using var directory = new TemporaryDirectory();
        var transport = new StubReadClient("""{"check_runs":[{"id":1,"name":"future","status":"completed","conclusion":"new-value"}]}""", """{"workflow_runs":[]}""");
        var service = new CiObservationService(new GitHubChecksWorkflowReader(transport), new GitHubActionsReader(transport));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ObserveAsync(directory.Path, Guid.NewGuid(), "o", "r"));
        var observed = await service.ObserveAsync(directory.Path, Guid.NewGuid(), "o", "r", "abcdef0123456789abcdef0123456789abcdef01");
        Assert.Equal("unknown", Assert.Single(observed.Checks).State);
        Assert.Equal("new-value", observed.Checks[0].ProviderConclusion);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ObserveAsync(directory.Path, Guid.NewGuid(), "o", "r", "abcdef0123456789abcdef0123456789abcdef01", 201));
        Assert.Equal(2, transport.Endpoints.Count);
    }

    [Fact]
    public async Task FailureEvidenceUsesLatestMatchingSnapshotAndExactFailedRunId()
    {
        using var directory = new TemporaryDirectory();
        var runId = Guid.NewGuid();
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        LocalRunEventStore.AppendCommitIdentifier(directory.Path, runId, sha);
        LocalRunEventStore.AppendCiCheckSnapshot(directory.Path, runId, sha, DateTimeOffset.UtcNow, [], [("10", "cancelled"), ("12", "failure")]);
        var run = $$"""{"id":12,"name":"CI","status":"completed","conclusion":"failure","event":"push","head_sha":"{{sha}}","html_url":"https://github.com/o/r/actions/runs/12"}""";
        var transport = new StubReadClient(run, """{"jobs":[{"id":4,"name":"build","conclusion":"action_required","steps":[{"name":"deploy","number":1,"conclusion":"failure"}]}]}""", "log");
        var service = new CiObservationService(new GitHubChecksWorkflowReader(transport), new GitHubActionsReader(transport));

        var evidence = await service.ReadLatestFailureEvidenceAsync(directory.Path, runId, "o", "r", 100);

        Assert.Equal(12, evidence!.RunId);
        Assert.Equal(new[] { "https://api.github.com/repos/o/r/actions/runs/12", "https://api.github.com/repos/o/r/actions/runs/12/jobs?per_page=200", "https://api.github.com/repos/o/r/actions/jobs/4/logs" }, transport.Endpoints.Select(endpoint => endpoint.ToString()));
    }

    sealed class StubReadClient(params string[] bodies) : IGitHubReadClient
    {
        int index;
        public List<Uri> Endpoints { get; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(index++, bodies.Length - 1)]) });
        }
    }

    sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
