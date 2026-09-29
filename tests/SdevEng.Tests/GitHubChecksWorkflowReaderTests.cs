namespace SdevEng.Tests;

public sealed class GitHubChecksWorkflowReaderTests
{
    [Fact]
    public async Task ReadsChecksAndWorkflowCollectionsThroughSharedTransport()
    {
        var transport = new StubReadClient(
            """{"check_runs":[{"id":12,"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/o/r/runs/12"}]}""",
            """{"workflows":[{"id":34,"name":"CI","state":"active","html_url":"https://github.com/o/r/actions/workflows/ci.yml"}]}""",
            """{"workflow_runs":[{"id":56,"name":"CI","status":"completed","conclusion":"success","html_url":"https://github.com/o/r/actions/runs/56"}]}""");
        var reader = new GitHubChecksWorkflowReader(transport);

        var checks = await reader.ReadChecksAsync("o", "r", "main");
        var workflows = await reader.ReadWorkflowsAsync("o", "r");
        var runs = await reader.ReadWorkflowRunsAsync("o", "r");

        Assert.Equal(new Uri("https://api.github.com/repos/o/r/commits/main/check-runs"), transport.Endpoints[0]);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/actions/workflows"), transport.Endpoints[1]);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/actions/runs"), transport.Endpoints[2]);
        Assert.Equal(new GitHubCheck(12, "build", "completed", "success", new Uri("https://github.com/o/r/runs/12")), Assert.Single(checks));
        Assert.Equal(new GitHubWorkflow(34, "CI", "active", new Uri("https://github.com/o/r/actions/workflows/ci.yml")), Assert.Single(workflows));
        Assert.Equal(new GitHubWorkflowRun(56, "CI", "completed", "success", new Uri("https://github.com/o/r/actions/runs/56")), Assert.Single(runs));
    }

    [Theory]
    [InlineData("../other", "repo", "main")]
    [InlineData("owner", "repo", "bad/ref")]
    public async Task RejectsUnsafeRouteSegments(string owner, string repository, string reference)
    {
        var transport = new StubReadClient("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubChecksWorkflowReader(transport).ReadChecksAsync(owner, repository, reference));
        Assert.Empty(transport.Endpoints);
    }

    sealed class StubReadClient(params string[] bodies) : IGitHubReadClient
    {
        int index;
        public List<Uri> Endpoints { get; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(bodies[index++]) });
        }
    }
}
