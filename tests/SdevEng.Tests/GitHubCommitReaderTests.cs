using System.Net;
using Microsoft.Extensions.DependencyInjection;

public sealed class GitHubCommitReaderTests
{
    [Fact]
    public async Task ReadsHeadAndCompareFactsThroughRegisteredHttpTransport()
    {
        var handler = new StubHandler();
        var services = new ServiceCollection();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton(new HttpClient(handler));
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<GitHubCommitReader>();

        var head = await reader.ReadHeadAsync("owner", "repo");
        Assert.Equal("abc123", head.Sha);
        Assert.Equal(new Uri("https://api.github.com/repos/owner/repo/commits/HEAD"), handler.Endpoints[0]);

        var compare = await reader.CompareToHeadAsync("owner", "repo", "base123");
        Assert.Equal(new Uri("https://api.github.com/repos/owner/repo/compare/base123...HEAD"), handler.Endpoints[1]);
        Assert.Equal("head456", compare.HeadCommitSha);
        Assert.Equal(new GitHubCompareFile("new.md", "renamed", "old.md"), Assert.Single(compare.Files));
    }

    [Theory]
    [InlineData("../other", "repo", "base")]
    [InlineData("owner", "repo?x=y", "base")]
    [InlineData("owner", "repo", "../head")]
    public async Task RejectsInvalidInputsBeforeRequest(string owner, string repository, string reference)
    {
        var client = new StubReadClient();
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubCommitReader(client).CompareToHeadAsync(owner, repository, reference));
        Assert.Null(client.Endpoint);
    }

    sealed class StubHandler : HttpMessageHandler
    {
        public List<Uri> Endpoints { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoints.Add(request.RequestUri!);
            var body = Endpoints.Count == 1
                ? """{"sha":"abc123"}"""
                : """{"head_commit":{"sha":"head456"},"files":[{"filename":"new.md","status":"renamed","previous_filename":"old.md"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    sealed class StubReadClient : IGitHubReadClient
    {
        public Uri? Endpoint { get; private set; }
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
