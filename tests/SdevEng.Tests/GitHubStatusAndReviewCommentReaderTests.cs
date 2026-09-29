using System.Net;

namespace SdevEng.Tests;

public sealed class GitHubStatusAndReviewCommentReaderTests
{
    [Fact]
    public async Task ReadsPrStatusFieldsAndCheckRollupThroughTransport()
    {
        var client = new StubReadClient("""{"head":{"ref":"feature/x"},"user":{"login":"alice"},"reviewDecision":"CHANGES_REQUESTED","statusCheckRollup":[{"name":"CI / test","status":"COMPLETED","conclusion":"SUCCESS","detailsUrl":"https://github.com/o/r/actions/runs/2","workflow":"CI"}]}""");
        var result = await new GitHubPrStatusReader(client).ReadAsync("o", "r", 9);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9"), client.Endpoint);
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
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/pulls/9/comments?per_page=100"), client.Endpoint);
    }

    sealed class StubReadClient(string body, string? link = null) : IGitHubReadClient
    {
        public Uri? Endpoint { get; private set; }
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            if (link is not null) response.Headers.TryAddWithoutValidation("Link", link);
            return Task.FromResult(response);
        }
    }
}
