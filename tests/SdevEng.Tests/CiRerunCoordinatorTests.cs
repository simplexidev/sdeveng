using System.Net;
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

        var result = await coordinator.RerunAsync(store.Path, runId, Sha, store.Path);

        Assert.Equal("rerun-requested", result.Status);
        Assert.Equal(HttpMethod.Post, writer.Method);
        Assert.Equal("https://api.github.com/repos/owner/repo/actions/jobs/91/rerun", writer.Endpoint!.ToString());
        var events = LocalRunEventStore.Read(store.Path, runId);
        var rerun = Assert.Single(events, item => item.GetProperty("eventType").GetString() == "ci-rerun");
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
        Assert.True(schema.Evaluate(JsonNode.Parse(rerun.GetRawText())!).IsValid);
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

        var result = await coordinator.RerunAsync(store.Path, runId, stale ? new string('a', 40) : Sha, store.Path);
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
        LocalRunEventStore.AppendCiRerunEvent(store.Path, runId, "owner/repo", Sha, "81", "91");
        var writer = new RecordingWriter();
        var result = await new CiRerunCoordinator(new Probe("allowed", "owner/repo"), writer).RerunAsync(store.Path, runId, Sha, store.Path);
        Assert.Equal("not-eligible", result.Status);
        Assert.Null(writer.Endpoint);
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
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
        {
            Method = method; Endpoint = endpoint;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
