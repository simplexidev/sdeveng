using System.Net;
using Microsoft.Extensions.DependencyInjection;

public sealed class GitHubReadClientTests
{
    [Fact]
    public async Task RegisteredReadClientRoutesTypedReadersThroughHttpTransport()
    {
        var handler = new StubHandler();
        var services = new ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("sdeveng");
        services.AddSingleton(http);
        using var provider = services.BuildServiceProvider();

        var metadata = await provider.GetRequiredService<GitHubRepositoryMetadataReader>().ReadAsync("owner", "repo");

        Assert.Equal("owner/repo", metadata.FullName);
        Assert.Equal(new Uri("https://api.github.com/repos/owner/repo"), handler.Endpoint);
        Assert.Equal("application/vnd.github+json", handler.Accept);
        Assert.Equal("sdeveng", handler.UserAgent);
        Assert.IsType<GitHubReadClient>(provider.GetRequiredService<IGitHubReadClient>());
        Assert.NotNull(provider.GetRequiredService<GitHubIssueReader>());
        Assert.NotNull(provider.GetRequiredService<GitHubChecksWorkflowReader>());
        Assert.NotNull(provider.GetRequiredService<GitHubPrStatusReader>());
        Assert.NotNull(provider.GetRequiredService<GitHubReviewCommentReader>());
    }

    [Theory]
    [InlineData("http://api.github.com/repos/o/r")]
    [InlineData("https://example.com/repos/o/r")]
    public async Task RejectsEndpointsOutsideHttpsGitHubApi(string endpoint)
    {
        using var http = new HttpClient(new StubHandler());
        var client = new GitHubReadClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync(new Uri(endpoint)));
    }

    sealed class StubHandler : HttpMessageHandler
    {
        public Uri? Endpoint { get; private set; }
        public string? Accept { get; private set; }
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            UserAgent = request.Headers.UserAgent.FirstOrDefault()?.Product?.Name;
            const string body = """{"id":1,"full_name":"owner/repo","html_url":"https://github.com/owner/repo","description":null,"default_branch":"main","private":false,"archived":false,"visibility":"public"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
