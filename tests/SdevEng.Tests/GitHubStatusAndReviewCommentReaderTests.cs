using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class GitHubStatusAndReviewCommentReaderTests
{
    [Fact]
    public async Task ReadsPrStatusFieldsAndCheckRollupThroughTransport()
    {
        var client = new StubReadClient("""{"head":{"ref":"feature/x"},"user":{"login":"alice"},"reviewDecision":"CHANGES_REQUESTED","statusCheckRollup":[{"name":"CI / test","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://github.com/o/r/actions/runs/2","workflow":"CI"}]}""");
        var result = await new GitHubPrStatusReader(client).ReadAsync("o", "r", 9);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9"), client.Endpoints[0]);
        Assert.Equal("feature/x", result.HeadBranch);
        Assert.Equal("alice", result.Author);
        Assert.Equal("CHANGES_REQUESTED", result.ReviewDecision);
        var check = Assert.Single(result.Checks);
        Assert.Equal("CI / test", check.Name);
        Assert.Equal("SUCCESS", check.State);
        Assert.Equal("https://github.com/o/r/actions/runs/2", check.Link?.ToString());
        Assert.Equal("CI", check.Workflow);
    }

    [Fact]
    public async Task FindsCurrentBranchPullRequestThenReadsItsStatus()
    {
        var client = new StubReadClient([
            """[{"number":9,"head":{"ref":"feature/x"}}]""",
            """{"head":{"ref":"feature/x"},"user":{"login":"alice"},"reviewDecision":"APPROVED","statusCheckRollup":[]}"""]);
        var result = await new GitHubPrStatusReader(client).ReadCurrentBranchAsync("o", "r", "feature/x");
        Assert.Equal("feature/x", result.HeadBranch);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9"), client.Endpoints[1]);
        Assert.Equal("https://api.github.com/repos/o/r/pulls?head=o%3Afeature%2Fx&state=open&per_page=100", client.Endpoints[0].AbsoluteUri);
    }

    [Fact]
    public async Task PrStatusCommandUsesCurrentBranchAndOriginThroughTypedReader()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/o/r.git");
        repo.Run("switch", "-c", "feature/x");
        var client = new StubReadClient([
            """[{"number":9,"head":{"ref":"feature/x"}}]""",
            """{"head":{"ref":"feature/x"},"reviewDecision":"APPROVED","statusCheckRollup":[]}"""]);
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(client);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(Cli.Parse(["github", "pr-status"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("ok", result.Status);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls?head=o%3Afeature%2Fx&state=open&per_page=100"), client.Endpoints[0]);
    }

    [Fact]
    public async Task ReviewCommentsCommandUsesRegisteredReaderAndWritesPaginatedArtifact()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/o/r.git");
        var secondPage = new Uri("https://api.github.com/repos/o/r/pulls/9/comments?page=2");
        var client = new StubReadClient([
            """[{"id":17,"body":"first"}]""",
            """[{"id":18,"body":"second"}]"""
        ], [secondPage]);
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<IGitHubReadClient>(client);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(Cli.Parse(["github", "review-comments", "--pr", "9"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9/comments?per_page=100"), client.Endpoints[0]);
        Assert.Equal(secondPage, client.Endpoints[1]);
        var report = Assert.IsType<ProcessReport>(result.Data);
        using var artifact = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(report.Artifact));
        Assert.Equal(new long[] { 17, 18 }, artifact.RootElement.EnumerateArray().Select(comment => comment.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task PreservesReviewCommentFieldsAndPagination()
    {
        var client = new StubReadClient("""[{"id":17,"pull_request_review_id":4,"node_id":"N","path":"src/a.cs","position":2,"original_position":3,"commit_id":"C","original_commit_id":"OC","user":{"login":"bob"},"body":"fix","created_at":"2026-01-02T03:04:05Z","updated_at":"2026-01-03T03:04:05Z","html_url":"https://github.com/o/r/pull/9#discussion_r17","pull_request_url":"https://api.github.com/repos/o/r/pulls/9","in_reply_to_id":12,"author_association":"MEMBER","start_line":1,"original_start_line":1,"start_side":"LEFT","line":2,"original_line":3,"side":"RIGHT","diff_hunk":"@@"}]""", "<https://api.github.com/repos/o/r/pulls/9/comments?page=2>; rel=\"next\"");
        var result = await new GitHubReviewCommentReader(client).ReadPageAsync("o", "r", 9);
        var comment = Assert.Single(result.Comments);
        Assert.Equal(17, comment.Id);
        Assert.Equal(4, comment.ReviewId);
        Assert.Equal("bob", comment.Author);
        Assert.Equal("fix", comment.Body);
        Assert.Equal(12, comment.InReplyToId);
        Assert.Equal("@@", comment.DiffHunk);
        Assert.Equal("RIGHT", comment.Side);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9/comments?page=2"), result.NextPage);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9/comments?per_page=100"), client.Endpoints[0]);
    }

    sealed class StubReadClient(string[] bodies, Uri[]? nextPages = null) : IGitHubReadClient
    {
        public StubReadClient(string body, string? link = null) : this([body], null) => Link = link;
        public List<Uri> Endpoints { get; } = [];
        string? Link { get; }
        int _index;
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            var index = _index++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[index]) };
            var link = index == 0 && nextPages is { Length: > 0 } ? $"<{nextPages[0]}>; rel=\"next\"" : Link;
            if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
            return Task.FromResult(response);
        }
    }
}
