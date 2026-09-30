using System.Text.RegularExpressions;
using System.Net;

public sealed class GitHubWriteBoundaryTests
{
    [Fact]
    public async Task AllowlistPermitsOnlyPullRequestCreationAndEditing()
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var client = new GitHubWriteClient(http);
        await client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/pulls"));
        await client.SendAsync(HttpMethod.Patch, new Uri("https://api.github.com/repos/owner/repo/pulls/12"));
        Assert.Equal(2, sent);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Put, "/repos/owner/repo/pulls/12/merge"),
            (HttpMethod.Post, "/repos/owner/repo/pulls/12/reviews"),
            (HttpMethod.Put, "/repos/owner/repo/branches/main/protection"),
            (HttpMethod.Delete, "/repos/owner/repo/pulls/12"),
            (HttpMethod.Patch, "/repos/owner/repo/pulls/0")
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(method, new Uri("https://api.github.com" + path)));
        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task BlocksAutoMergeEnablementAndApprovalBeforeSending()
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var client = new GitHubWriteClient(http);
        using var autoMerge = new StringContent("{\"query\":\"mutation { enablePullRequestAutoMerge(input: {}) { clientMutationId } }\"}");
        using var approval = new StringContent("{\"event\":\"APPROVE\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post,
            new Uri("https://api.github.com/graphql"), autoMerge));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post,
            new Uri("https://api.github.com/repos/owner/repo/pulls/12/reviews"), approval));
        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData("PUT", "/repos/owner/repo/branches/main/protection")]
    [InlineData("PATCH", "/repos/owner/repo/branches/main/protection")]
    [InlineData("DELETE", "/repos/owner/repo/branches/main/protection")]
    [InlineData("DELETE", "/repos/owner/repo/branches/main/protection/required_status_checks")]
    [InlineData("DELETE", "/repos/owner/repo/branches/main/protection/enforce_admins")]
    [InlineData("DELETE", "/repos/owner/repo/branches/main/protection/required_pull_request_reviews")]
    public async Task BlocksBranchProtectionWeakeningBeforeSending(string verb, string path)
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var client = new GitHubWriteClient(http);
        using var content = new StringContent("{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(
            new HttpMethod(verb), new Uri("https://api.github.com" + path), content));
        Assert.Equal(0, sent);
    }

    sealed class RecordingHandler(Action record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            record();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void ProductionWritesUseOnlyGuardedBoundary()
    {
        var source = File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tools/AgentTool.cs"));

        Assert.Equal(2, Regex.Matches(source, @"\bIGitHubWriteClient\b").Count);
        Assert.Contains("public sealed class GitHubWriteClient(HttpClient http) : IGitHubWriteClient", source);
        Assert.Contains("Processes.Run(\"gh\", [\"auth\", \"status\"]", source);
        Assert.DoesNotContain("Processes.Run(\"gh\", [\"pr\", \"", source);
    }
}
