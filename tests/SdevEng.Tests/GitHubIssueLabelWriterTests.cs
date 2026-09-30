using System.Net;
using System.Text.Json;

namespace SdevEng.Tests;

public sealed class GitHubIssueLabelWriterTests
{
    [Fact]
    public async Task AddsOnlyConfiguredTriageAndSelectedTypeLabelsToCanonicalIssue()
    {
        var transport = new FakeWriteClient();
        var writer = new GitHubIssueLabelWriter(transport);
        using var catalog = Catalog();
        var result = await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(), catalog.RootElement);

        Assert.Equal("applied", result.Status);
        Assert.Equal(new[] { "TRIAGED", "type:chore" }, result.Labels);
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/labels", transport.Endpoint!.AbsoluteUri);
        Assert.Equal("application/json", transport.ContentType);
        Assert.Equal(new[] { "TRIAGED", "type:chore" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("IN_PROGRESS", transport.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("denied")]
    public async Task UnknownOrDeniedCapabilitySendsNothing(string state)
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var result = await new GitHubIssueLabelWriter(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(state), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task ReviewUnresolvedRepositoryMismatchAndUnconfiguredTypeSendNothing()
    {
        var transport = new FakeWriteClient();
        var writer = new GitHubIssueLabelWriter(transport);
        using var catalog = Catalog();
        Assert.Equal("review", (await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved() with { NeedsHumanReview = true }, Allowed(), catalog.RootElement)).Status);
        Assert.Equal("review", (await writer.AddTriageLabelsAsync("acme/widget", "acme/other", 42, Resolved(), Allowed(), catalog.RootElement)).Status);
        var invalid = Resolved() with { Selected = [new("type", [new("type:made-up", 1, ["x"])])] };
        Assert.Equal("failure", (await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, invalid, Allowed(), catalog.RootElement)).Status);
        Assert.Null(transport.Endpoint);
    }

    static JsonDocument Catalog() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "config/labels.json")));
    static TriageDecision Resolved() => new([new("type", [new("type:chore", 1, ["explicit"])])], [], false);
    static AgentTool.GitHubCapabilities Allowed(string state = "allowed") => new("github-capabilities", [new("issues.labels.write", "acme/widget", state, "fake", "test")]);

    sealed class FakeWriteClient : IGitHubWriteClient
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
        {
            Method = method;
            Endpoint = endpoint;
            Body = content is null ? null : await content.ReadAsStringAsync(cancellationToken);
            ContentType = content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
