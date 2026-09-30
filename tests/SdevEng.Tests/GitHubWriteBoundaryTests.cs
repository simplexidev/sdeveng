using System.Text.RegularExpressions;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

public sealed class GitHubWriteBoundaryTests
{
    [Fact]
    public async Task AllowlistPermitsOwnedPullRequestAndIssueLabelEndpointsOnly()
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var client = new GitHubWriteClient(http, new FakeCredentials("secret-token"));
        await client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/pulls"));
        await client.SendAsync(HttpMethod.Patch, new Uri("https://api.github.com/repos/owner/repo/pulls/12"));
        await client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/issues/12/labels"), new StringContent("{\"labels\":[\"TRIAGED\"]}"));
        await client.SendAsync(HttpMethod.Delete, new Uri("https://api.github.com/repos/owner/repo/issues/12/labels/READY"));
        await new GitHubActionsJobRerunWriter(client).RerunAsync("owner/repo", "91");
        await new GitHubActionsFailedJobsRerunWriter(client).RerunAsync("owner/repo", "81");
        Assert.Equal(6, sent);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Put, "/repos/owner/repo/pulls/12/merge"),
            (HttpMethod.Post, "/repos/owner/repo/pulls/12/reviews"),
            (HttpMethod.Put, "/repos/owner/repo/branches/main/protection"),
            (HttpMethod.Delete, "/repos/owner/repo/pulls/12"),
            (HttpMethod.Patch, "/repos/owner/repo/pulls/0")
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(method, new Uri("https://api.github.com" + path)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Put, new Uri("https://api.github.com/repos/owner/repo/issues/12/labels")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Delete, new Uri("https://api.github.com/repos/owner/repo/issues/12/labels/TRIAGED")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/issues/0/labels")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/actions/jobs/91/rerun/extra")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/actions/runs/81/rerun")));
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubActionsJobRerunWriter(client).RerunAsync("owner/repo", "0"));
        Assert.Equal(6, sent);
    }

    [Fact]
    public async Task BlocksAutoMergeEnablementAndApprovalBeforeSending()
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var client = new GitHubWriteClient(http, new FakeCredentials("secret-token"));
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
        var client = new GitHubWriteClient(http, new FakeCredentials("secret-token"));
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

    sealed class FakeCredentials(string? token) : IGitHubCredentialProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    [Fact]
    public async Task SendsAuthenticatedRequestAndDoesNotExposeCredentialOnFailure()
    {
        const string token = "synthetic-secret-token";
        string? authorization = null;
        using var http = new HttpClient(new InspectingHandler(request => authorization = request.Headers.Authorization?.ToString(), fail: true));
        var client = new GitHubWriteClient(http, new FakeCredentials(token));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/pulls")));
        Assert.Equal("Bearer " + token, authorization);
        Assert.DoesNotContain(token, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentialAndDeniedEndpointNeverSend()
    {
        var sent = 0;
        using var http = new HttpClient(new RecordingHandler(() => sent++));
        var missing = new GitHubWriteClient(http, new FakeCredentials(null));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SendAsync(HttpMethod.Post, new Uri("https://api.github.com/repos/owner/repo/pulls")));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        var denied = new GitHubWriteClient(http, new FakeCredentials("unused"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SendAsync(HttpMethod.Put, new Uri("https://api.github.com/repos/owner/repo/pulls/1/merge")));
        Assert.Equal(0, sent);
    }

    sealed class InspectingHandler(Action<HttpRequestMessage> inspect, bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect(request);
            if (fail) throw new HttpRequestException("synthetic-secret-token");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void ProductionRegistrationResolvesAuthenticatedWriteBoundary()
    {
        var services = new ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<GitHubWriteClient>(provider.GetRequiredService<IGitHubWriteClient>());
        Assert.IsType<GitHubIssueLabelWriter>(provider.GetRequiredService<GitHubIssueLabelWriter>());
        Assert.IsType<GitHubCredentialProvider>(provider.GetRequiredService<IGitHubCredentialProvider>());
    }

    [Fact]
    public void ProductionWritesUseOnlyGuardedBoundary()
    {
        var source = File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tools/AgentTool.cs"));

        Assert.Equal(8, Regex.Matches(source, @"\bIGitHubWriteClient\b").Count);
        Assert.Contains("public sealed class GitHubWriteClient(HttpClient http, IGitHubCredentialProvider credentials) : IGitHubWriteClient", source);
        Assert.Contains("Processes.Run(\"gh\", [\"auth\", \"status\"]", source);
        Assert.DoesNotContain("Processes.Run(\"gh\", [\"pr\", \"", source);
    }
}
