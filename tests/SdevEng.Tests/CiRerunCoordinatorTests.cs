using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

public sealed class CiRerunCoordinatorTests
{
    const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task EligibleEvidenceSendsOneExactPostAndPersistsRerunEvent()
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", "timeout", false, "job timed out");
        var writer = new RecordingWriter();
        var coordinator = new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer);

        var result = await coordinator.RerunAsync(store.Path, runId, Sha, store.Path, "Retry after timeout");

        Assert.Equal("rerun-requested", result.Status);
        Assert.Equal(HttpMethod.Post, writer.Method);
        Assert.Equal("https://api.github.com/repos/owner/repo/actions/jobs/91/rerun", writer.Endpoint!.ToString());
        Assert.Equal(1, writer.Attempts);
        var events = LocalRunEventStore.Read(store.Path, runId);
        var rerun = Assert.Single(events, item => item.GetProperty("eventType").GetString() == "ci-rerun");
        Assert.Equal(1, rerun.GetProperty("ordinal").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", rerun.GetProperty("failureSignature").GetString());
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
        Assert.True(schema.Evaluate(JsonNode.Parse(rerun.GetRawText())!).IsValid);
        var explanation = JsonSerializer.SerializeToNode(LocalRunEventStore.Explain(store.Path, runId), AgentTool.Json)!;
        Assert.Equal(1, explanation["rerunMetrics"]!["total"]!.GetValue<int>());
        Assert.Equal(1, explanation["rerunMetrics"]!["currentFailureIdentity"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("timeout", "denied", false, false)]
    [InlineData("timeout", "unknown", false, false)]
    [InlineData("code", "allowed", false, false)]
    [InlineData("test", "allowed", false, false)]
    [InlineData("format", "allowed", false, false)]
    [InlineData("infrastructure", "allowed", true, false)]
    [InlineData("timeout", "allowed", false, true)]
    public async Task IneligibleEvidenceNeverPosts(string failureClass, string capability, bool stale, bool missingIds)
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        if (!missingIds) LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", failureClass, false, "failure");
        var writer = new RecordingWriter();
        var coordinator = new CiRerunCoordinator(new Probe(capability, "owner/repo"), writer);

        var result = await coordinator.RerunAsync(store.Path, runId, stale ? new string('a', 40) : Sha, store.Path, "retry");
        Assert.Equal("not-eligible", result.Status);
        Assert.Null(writer.Endpoint);
    }

    [Fact]
    public async Task DuplicateFailureIdentityDoesNotPostAgain()
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", "infrastructure", false, "failure");
        LocalRunEventStore.AppendCiRerunEvent(store.Path, runId, "owner/repo", Sha, "81", "91", "job", "retry");
        var writer = new RecordingWriter();
        var result = await new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer).RerunAsync(store.Path, runId, Sha, store.Path, "retry");
        Assert.Equal("not-eligible", result.Status);
        Assert.Null(writer.Endpoint);
    }

    [Fact]
    public void FailureSignatureIsStableAndContainsNoFailureText()
    {
        var first = LocalRunEventStore.ComputeCiFailureSignature("Owner/Repo", Sha, "81", "91", "timeout");
        var second = LocalRunEventStore.ComputeCiFailureSignature("owner/repo", Sha.ToUpperInvariant(), "81", "91", "timeout");
        Assert.Equal(first, second);
        Assert.DoesNotContain("secret", first, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first, LocalRunEventStore.ComputeCiFailureSignature("owner/repo", Sha, "82", "91", "timeout"));
    }

    [Fact]
    public async Task ASecondIdenticalRequestIsBlockedWithoutSending()
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", "timeout", false, "failure");
        var writer = new RecordingWriter();
        var coordinator = new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer);
        Assert.Equal("rerun-requested", (await coordinator.RerunAsync(store.Path, runId, Sha, store.Path, "retry")).Status);
        Assert.Equal("not-eligible", (await coordinator.RerunAsync(store.Path, runId, Sha, store.Path, "retry")).Status);
        Assert.Equal(1, writer.Attempts);
        var rerun = Assert.Single(LocalRunEventStore.Read(store.Path, runId), item => item.GetProperty("eventType").GetString() == "ci-rerun");
        Assert.Equal(1, rerun.GetProperty("ordinal").GetInt32());
    }

    [Fact]
    public async Task UnsupportedJobEndpointFallsBackOnceAndPersistsModeAndRedactedReason()
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", "timeout", false, "failure");
        var writer = new RecordingWriter(HttpStatusCode.NotFound, HttpStatusCode.Accepted);
        var result = await new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer)
            .RerunAsync(store.Path, runId, Sha, store.Path, "retry token=secret");
        Assert.Equal("rerun-requested", result.Status);
        Assert.Equal(2, writer.Attempts);
        Assert.Equal(new[] { "https://api.github.com/repos/owner/repo/actions/jobs/91/rerun", "https://api.github.com/repos/owner/repo/actions/runs/81/rerun-failed-jobs" }, writer.Endpoints.Select(x => x.ToString()));
        var rerun = Assert.Single(LocalRunEventStore.Read(store.Path, runId), x => x.GetProperty("eventType").GetString() == "ci-rerun");
        Assert.Equal("failed-jobs", rerun.GetProperty("rerunMode").GetString());
        Assert.DoesNotContain("secret", rerun.GetProperty("rerunReason").GetString());
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
        Assert.True(schema.Evaluate(JsonNode.Parse(rerun.GetRawText())!).IsValid);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task JobFailureOtherThanUnsupportedDoesNotFallback(HttpStatusCode status)
    {
        using var store = new TempDirectory();
        var runId = Guid.NewGuid();
        LocalRunEventStore.AppendRepositoryIdentifier(store.Path, runId, "owner/repo");
        LocalRunEventStore.AppendCiFailureEvidence(store.Path, runId, Sha, "81", "91", "timeout", false, "failure");
        var writer = new RecordingWriter(status);
        var result = await new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer).RerunAsync(store.Path, runId, Sha, store.Path, "retry");
        Assert.Equal("not-eligible", result.Status);
        Assert.Equal(1, writer.Attempts);
    }

    [Fact]
    public void ProductionRegistrationResolvesCoordinatorAndTypedWriter()
    {
        var services = new ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<CiRerunCoordinator>(provider.GetRequiredService<CiRerunCoordinator>());
        Assert.IsType<GitHubActionsJobRerunWriter>(provider.GetRequiredService<GitHubActionsJobRerunWriter>());
        Assert.IsType<AgentTool.GitHubAuthorizationProbe>(provider.GetRequiredService<IGitHubAuthorizationProbe>());
    }

    sealed class Probe(string state, string target) : IGitHubAuthorizationProbe
    {
        public Task<AgentTool.GitHubCapabilities> ProbeAsync(string root, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentTool.GitHubCapabilities("github-capabilities", [new("workflow-rerun", target, state, "fake", "fake evidence")]));
    }

    sealed class RecordingWriter : IGitHubWriteClient
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Endpoint { get; private set; }
        public List<Uri> Endpoints { get; } = [];
        public int Attempts { get; private set; }
        readonly HttpStatusCode[] statuses;
        int next;
        public RecordingWriter(params HttpStatusCode[] statuses) => this.statuses = statuses.Length == 0 ? [HttpStatusCode.NoContent] : statuses;
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
        {
            Method = method; Endpoint = endpoint; Endpoints.Add(endpoint); Attempts++;
            return Task.FromResult(new HttpResponseMessage(statuses[Math.Min(next++, statuses.Length - 1)]));
        }
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
