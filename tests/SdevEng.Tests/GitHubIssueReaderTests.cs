using System.Net;
using System.Text.Json;

public sealed class GitHubIssueReaderTests
{
    const string Issue = """
        {"number":7,"title":"Fix the thing","state":"open","html_url":"https://github.com/owner/project/issues/7","body":"Details","user":{"login":"octocat"}}
        """;
    const string PullRequest = """
        {"number":8,"title":"Implement the thing","state":"closed","html_url":"https://github.com/owner/project/pull/8","body":null,"user":{"login":"octocat"},"base":{"ref":"main"},"head":{"ref":"feature"},"draft":false,"merged":true}
        """;

    [Fact]
    public async Task ReadsIssueFromExplicitEndpoint()
    {
        var transport = new StubReadClient(Issue);
        var result = await new GitHubIssueReader(transport).ReadIssueAsync("owner", "project", 7);

        Assert.Equal(new Uri("https://api.github.com/repos/owner/project/issues/7"), transport.Endpoint);
        Assert.Equal(new GitHubIssue(7, "Fix the thing", "open", new Uri("https://github.com/owner/project/issues/7"), "Details", "octocat"), result);
    }

    [Fact]
    public async Task ReadsTypedIssueLabelNames()
    {
        const string response = """{"number":7,"title":"Issue","state":"open","html_url":"https://github.com/owner/project/issues/7","labels":[{"name":"customer"},{"name":"IN_PROGRESS"}]}""";
        var transport = new StubReadClient(response);
        var labels = await new GitHubIssueReader(transport).ReadIssueLabelNamesAsync("owner", "project", 7);
        Assert.Equal(new[] { "customer", "IN_PROGRESS" }, labels);
        Assert.Equal(new Uri("https://api.github.com/repos/owner/project/issues/7"), transport.Endpoint);
    }

    [Fact]
    public async Task NormalizesIssueTitleAndBodyBoundariesAndLineEndings()
    {
        const string response = """
            {"number":7,"title":"  Fix the thing\r\n ","state":"open","html_url":"https://github.com/owner/project/issues/7","body":"  First line\r\nSecond line\rThird line  ","user":{"login":"octocat"}}
            """;
        var result = await new GitHubIssueReader(new StubReadClient(response)).ReadIssueAsync("owner", "project", 7);

        Assert.Equal("Fix the thing", result.Title);
        Assert.Equal("First line\nSecond line\nThird line", result.Body);
    }

    [Fact]
    public async Task ReadsPullRequestFromExplicitEndpoint()
    {
        var transport = new StubReadClient(PullRequest);
        var result = await new GitHubIssueReader(transport).ReadPullRequestAsync("owner", "project", 8);

        Assert.Equal(new Uri("https://api.github.com/repos/owner/project/pulls/8"), transport.Endpoint);
        Assert.Equal(new GitHubPullRequest(8, "Implement the thing", "closed", new Uri("https://github.com/owner/project/pull/8"), null, "octocat", "main", "feature", false, true), result);
    }

    [Fact]
    public async Task RejectsInvalidTargetsBeforeRequest()
    {
        var transport = new StubReadClient(Issue);
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubIssueReader(transport).ReadIssueAsync("../other", "project", 7));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new GitHubIssueReader(transport).ReadPullRequestAsync("owner", "project", 0));
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task RejectsIncompletePullRequestResponse()
    {
        var reader = new GitHubIssueReader(new StubReadClient("{}"));
        await Assert.ThrowsAsync<JsonException>(() => reader.ReadPullRequestAsync("owner", "project", 8));
    }

    sealed class StubReadClient(string body) : IGitHubReadClient
    {
        public Uri? Endpoint { get; private set; }

        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
