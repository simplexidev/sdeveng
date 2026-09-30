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
        LocalRunEventStore.AppendStepCommitIdentifier(directory.Path, runId, sha);
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
