using System.Net;
using System.Text.Json;

namespace SdevEng.Tests;

public sealed class GitHubLabelTests
{
    [Fact]
    public async Task SynchronizesMissingAndStaleConfiguredLabelsThroughTypedTransportOnlyWhenApplied()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var writes = new RecordingWriter();
        using var http = new HttpClient(new LabelsHandler());
        var catalog = new GitHubLabelCatalog(http, new StaticCredential(), writes);
        var module = new AgentTool.GitHubCommandModule(labelCatalog: catalog);

        var listed = await module.Execute(Cli.Parse(["github", "labels"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);
        var dry = JsonSerializer.SerializeToElement(listed.Data, AgentTool.Json);
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(13, dry.GetProperty("missing").GetArrayLength());
        Assert.Single(dry.GetProperty("stale").EnumerateArray());
        Assert.Equal("custom:keep", dry.GetProperty("unmanaged")[0].GetString());
        Assert.Empty(writes.Calls);

        var applied = await module.Execute(Cli.Parse(["github", "labels", "--apply"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);
        var result = JsonSerializer.SerializeToElement(applied.Data, AgentTool.Json);
        Assert.Equal(13, result.GetProperty("created").GetArrayLength());
        Assert.Single(result.GetProperty("updated").EnumerateArray());
        Assert.Empty(result.GetProperty("missing").EnumerateArray());
        Assert.Equal(13, writes.Calls.Count(call => call.Method == HttpMethod.Post));
        Assert.Single(writes.Calls, call => call.Method == HttpMethod.Patch);
        Assert.DoesNotContain(writes.Calls, call => call.Method == HttpMethod.Delete);
        Assert.All(writes.Calls, call =>
        {
            Assert.Contains("\"description\"", call.Body, StringComparison.Ordinal);
            Assert.Contains("\"color\"", call.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RejectsConflictingApplyAndDryRunOptions()
    {
        var command = Cli.Parse(["github", "labels", "--dry-run", "--apply"]);
        Assert.Throws<ArgumentException>(() => command.ValidateCommand(command.Command));
    }

    private sealed class LabelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer synthetic-token", request.Headers.Authorization?.ToString());
            var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "config", "labels.json")));
            var first = catalog.RootElement.GetProperty("labels")[0];
            var name = first.GetProperty("name").GetString()!;
            var body = JsonSerializer.Serialize(new[]
            {
                new { name, description = "stale description", color = "000000" },
                new { name = "custom:keep", description = "unmanaged", color = "abcdef" }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class StaticCredential : IGitHubCredentialProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("synthetic-token");
    }

    private sealed class RecordingWriter : IGitHubWriteClient
    {
        public List<(HttpMethod Method, Uri Endpoint, string Body)> Calls { get; } = [];
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((method, endpoint, content is null ? "" : await content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
