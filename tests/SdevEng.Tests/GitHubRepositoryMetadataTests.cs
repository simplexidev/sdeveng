using System.Net;

public sealed class GitHubRepositoryMetadataTests
{
    const string Response = """
        {"id":123,"full_name":"owner/project","html_url":"https://github.com/owner/project","description":"A project","default_branch":"main","private":false,"archived":false,"visibility":"public","pushed_at":"2026-09-28T12:00:00Z","updated_at":"2026-09-28T12:30:00Z"}
        """;

    [Fact]
    public async Task ReadsTypedMetadataFromExplicitRepositoryEndpoint()
    {
        var transport = new StubReadClient(Response);
        var result = await new GitHubRepositoryMetadataReader(transport).ReadAsync("owner", "project");

        Assert.Equal(new Uri("https://api.github.com/repos/owner/project"), transport.Endpoint);
        Assert.Equal(123, result.Id);
        Assert.Equal("owner/project", result.FullName);
        Assert.Equal("main", result.DefaultBranch);
        Assert.Equal("A project", result.Description);
        Assert.False(result.IsPrivate);
        Assert.False(result.IsArchived);
        Assert.Equal("public", result.Visibility);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), result.PushedAt);
    }

    [Theory]
    [InlineData("../other", "repo")]
    [InlineData("owner", "repo?per_page=100")]
    public async Task RejectsInvalidRepositorySegmentsBeforeRequest(string owner, string repository)
    {
        var transport = new StubReadClient(Response);
        await Assert.ThrowsAsync<ArgumentException>(() => new GitHubRepositoryMetadataReader(transport).ReadAsync(owner, repository));
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task RejectsIncompleteApiResponse()
    {
        var reader = new GitHubRepositoryMetadataReader(new StubReadClient("{}"));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => reader.ReadAsync("owner", "repo"));
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
