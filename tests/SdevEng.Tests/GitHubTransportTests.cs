using System.Net;

public sealed class GitHubTransportTests
{
    [Fact]
    public async Task NormalizesHttpStatusWithoutExposingResponseBody()
    {
        var client = new StubReadClient(() => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private response") });

        var error = await Assert.ThrowsAsync<GitHubTransportException>(() => GitHubTransport.GetAsync(client, new Uri("https://api.github.com/repos/o/r")));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("private response", error.ToString());
    }

    [Fact]
    public async Task NormalizesTransportErrors()
    {
        var client = new StubReadClient(() => throw new HttpRequestException("socket failure"));

        var error = await Assert.ThrowsAsync<GitHubTransportException>(() => GitHubTransport.GetAsync(client, new Uri("https://api.github.com/repos/o/r")));

        Assert.Contains("transport layer", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    sealed class StubReadClient(Func<HttpResponseMessage> response) : IGitHubReadClient
    {
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default) => Task.FromResult(response());
    }
}
