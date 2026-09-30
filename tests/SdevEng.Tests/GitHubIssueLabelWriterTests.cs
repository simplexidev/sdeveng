using System.Net;
using System.Text.Json;

namespace SdevEng.Tests;

public sealed class GitHubIssueLabelWriterTests
{
    [Fact]
    public async Task AddsOnlyDistinctConfiguredAreasToCanonicalIssue()
    {
        var transport = new FakeWriteClient();
        var writer = new GitHubIssueLabelWriter(transport);
        using var catalog = Catalog();
        var result = await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(), catalog.RootElement);

        Assert.Equal("applied", result.Status);
        Assert.Equal(new[] { "area:tooling" }, result.Labels);
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/labels", transport.Endpoint!.AbsoluteUri);
        Assert.Equal("application/json", transport.ContentType);
        Assert.Equal(new[] { "area:tooling" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
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
        Assert.Equal("review", (await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, new TriageDecision([], ["area"], true), Allowed(), catalog.RootElement)).Status);
        Assert.Equal("review", (await writer.AddTriageLabelsAsync("acme/widget", "acme/other", 42, Resolved(), Allowed(), catalog.RootElement)).Status);
        var invalid = Resolved() with { Selected = [new("area", [new("area:made-up", 1, ["x"])])] };
        Assert.Equal("failure", (await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, invalid, Allowed(), catalog.RootElement)).Status);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task AddsMultipleAreasAndDeduplicatesWithoutReplacingUnrelatedLabels()
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"area:tooling","family":"area"},{"name":"area:docs","family":"area"}]}""");
        var decision = new TriageDecision([new("area", [new("area:tooling", 1, ["a"]), new("area:docs", 1, ["b"]), new("area:tooling", 1, ["c"])])], [], false);
        var transport = new FakeWriteClient();
        var result = await new GitHubIssueLabelWriter(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal(new[] { "area:tooling", "area:docs" }, result.Labels);
        Assert.Equal(new[] { "area:tooling", "area:docs" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("existing-label", transport.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyAreaSelectionPreservesUnresolvedStatusWithoutRequest()
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var result = await new GitHubIssueLabelWriter(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42,
            new TriageDecision([], ["area"], true), Allowed(), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Null(transport.Endpoint);
    }

    static JsonDocument Catalog() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "config/labels.json")));
    static TriageDecision Resolved() => new([new("area", [new("area:tooling", 1, ["explicit"])])], [], false);
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
