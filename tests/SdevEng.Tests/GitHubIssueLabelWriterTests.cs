using System.Net;
using System.Text.Json;

namespace SdevEng.Tests;

public sealed class GitHubIssueLabelWriterTests
{
    [Fact]
    public async Task AddsOnlyDistinctConfiguredAreasToCanonicalIssue()
    {
        var transport = new FakeWriteClient();
        var writer = Writer(transport);
        using var catalog = Catalog();
        var result = await writer.AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(), catalog.RootElement);

        Assert.Equal("applied", result.Status);
        Assert.Equal(new[] { "area:tooling" }, result.Labels);
        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/labels", transport.Endpoint!.AbsoluteUri);
        Assert.Equal("application/json", transport.ContentType);
        Assert.Equal(new[] { "area:tooling" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task ExistingInProgressRefusesTriageBeforeMutation()
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var reader = new GitHubIssueReader(new StubIssueReadClient("IN_PROGRESS"));
        var result = await new GitHubIssueLabelWriter(transport, reader).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task NoOpTriageReadsAndPreservesExistingLabels()
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var read = new StubIssueReadClient("customer-label");
        var result = await new GitHubIssueLabelWriter(transport, new GitHubIssueReader(read)).AddTriageLabelsAsync("acme/widget", "acme/widget", 42,
            new TriageDecision([], ["area"], true), Allowed(), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Equal(2, read.ReadCount);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task InProgressPostconditionReturnsReviewAfterLabelAdd()
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var read = new StubIssueReadClient("", "IN_PROGRESS");
        var result = await new GitHubIssueLabelWriter(transport, new GitHubIssueReader(read)).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.NotNull(transport.Endpoint);
        Assert.Equal(2, read.ReadCount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("denied")]
    public async Task UnknownOrDeniedCapabilitySendsNothing(string state)
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, Resolved(), Allowed(state), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task ReviewUnresolvedRepositoryMismatchAndUnconfiguredTypeSendNothing()
    {
        var transport = new FakeWriteClient();
        var writer = Writer(transport);
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
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal(new[] { "area:tooling", "area:docs" }, result.Labels);
        Assert.Equal(new[] { "area:tooling", "area:docs" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("existing-label", transport.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadyRemovalRequiresConfiguredStatusAndDeletesOnlyReady()
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"READY","family":"status"}]}""");
        var write = new ReadyWriteClient();
        var readClient = new ReadyReadClient("READY,customer-label", "customer-label");
        var reader = new GitHubIssueReader(readClient);
        var result = await new GitHubIssueLabelWriter(write, reader).RemoveConfiguredReadyLabelAsync("acme/widget", 42, Allowed(), catalog.RootElement);
        Assert.Equal("applied", result.Status);
        Assert.Equal(HttpMethod.Delete, write.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/labels/READY", write.Endpoint!.AbsoluteUri);
        Assert.Equal(new[] { "customer-label" }, readClient.LastLabels);

        using var unconfigured = JsonDocument.Parse("""{"labels":[]}""");
        var noWrite = new ReadyWriteClient();
        var noOp = await new GitHubIssueLabelWriter(noWrite, new GitHubIssueReader(new ReadyReadClient("READY")))
            .RemoveConfiguredReadyLabelAsync("acme/widget", 42, Allowed(), unconfigured.RootElement);
        Assert.Equal("no-op", noOp.Status);
        Assert.Null(noWrite.Method);

        var absent = new ReadyWriteClient();
        var absentResult = await new GitHubIssueLabelWriter(absent, new GitHubIssueReader(new ReadyReadClient("customer-label")))
            .RemoveConfiguredReadyLabelAsync("acme/widget", 42, Allowed(), catalog.RootElement);
        Assert.Equal("no-op", absentResult.Status);
        Assert.Null(absent.Method);
    }

    [Fact]
    public async Task ReadyDeletionFailureIsReportedAndUnrelatedLabelsArePreserved()
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"READY","family":"status"}]}""");
        var write = new ReadyWriteClient(HttpStatusCode.Forbidden);
        var result = await new GitHubIssueLabelWriter(write, new GitHubIssueReader(new ReadyReadClient("READY,customer-label")))
            .RemoveConfiguredReadyLabelAsync("acme/widget", 42, Allowed(), catalog.RootElement);
        Assert.Equal("failure", result.Status);
        Assert.Equal(HttpMethod.Delete, write.Method);
        Assert.Equal("https://api.github.com/repos/acme/widget/issues/42/labels/READY", write.Endpoint!.AbsoluteUri);
    }

    [Fact]
    public async Task EmptyAreaSelectionPreservesUnresolvedStatusWithoutRequest()
    {
        var transport = new FakeWriteClient();
        using var catalog = Catalog();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42,
            new TriageDecision([], ["area"], true), Allowed(), catalog.RootElement);
        Assert.Equal("review", result.Status);
        Assert.Null(transport.Endpoint);
    }

    [Fact]
    public async Task AddsOnlySelectedConfiguredRiskAndComplexityLabels()
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"risk:high","family":"risk"},{"name":"complexity:low","family":"complexity"},{"name":"type:bug","family":"type"},{"name":"area:docs","family":"area"}]}""");
        var decision = new TriageDecision([
            new("risk", [new("risk:high", 1, ["evidence"])]),
            new("complexity", [new("complexity:low", 1, ["evidence"])]),
            new("type", [new("type:bug", 1, ["must-not-apply"])]),
            new("area", [new("area:docs", 1, ["must-not-apply"])])
        ], [], false);
        var transport = new FakeWriteClient();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal("applied", result.Status);
        Assert.Equal(new[] { "area:docs", "risk:high", "complexity:low" }, result.Labels);
        Assert.Equal(new[] { "area:docs", "risk:high", "complexity:low" }, JsonDocument.Parse(transport.Body!).RootElement.GetProperty("labels").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData("risk")]
    [InlineData("complexity")]
    public async Task UnresolvedFamilyIsUntouchedWhileOtherResolvedFamilyApplies(string unresolvedFamily)
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"risk:high","family":"risk"},{"name":"complexity:low","family":"complexity"}]}""");
        var selected = unresolvedFamily == "risk" ? "complexity" : "risk";
        var label = selected == "risk" ? "risk:high" : "complexity:low";
        var decision = new TriageDecision([new(selected, [new(label, 1, ["explicit"])])], [unresolvedFamily], true);
        var transport = new FakeWriteClient();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal("applied", result.Status);
        Assert.Equal(new[] { label }, result.Labels);
    }

    [Theory]
    [InlineData("risk", "risk:low", "risk:high")]
    [InlineData("complexity", "complexity:low", "complexity:high")]
    public async Task RejectsMultipleSelectionsInExclusiveFamilies(string family, string first, string second)
    {
        using var catalog = JsonDocument.Parse($"{{\"labels\":[{{\"name\":\"{first}\",\"family\":\"{family}\"}},{{\"name\":\"{second}\",\"family\":\"{family}\"}}]}}");
        var decision = new TriageDecision([new(family, [new(first, 1, []), new(second, 1, [])])], [], false);
        var transport = new FakeWriteClient();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal("failure", result.Status);
        Assert.Null(transport.Endpoint);
    }

    [Theory]
    [InlineData("risk", "risk:unconfigured")]
    [InlineData("complexity", "complexity:unconfigured")]
    public async Task RejectsUnconfiguredSelectedFamilyLabels(string family, string label)
    {
        using var catalog = JsonDocument.Parse("""{"labels":[{"name":"risk:high","family":"risk"},{"name":"complexity:low","family":"complexity"}]}""");
        var decision = new TriageDecision([new(family, [new(label, 1, [])])], [], false);
        var transport = new FakeWriteClient();
        var result = await Writer(transport).AddTriageLabelsAsync("acme/widget", "acme/widget", 42, decision, Allowed(), catalog.RootElement);
        Assert.Equal("failure", result.Status);
        Assert.Null(transport.Endpoint);
    }

    static JsonDocument Catalog() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "config/labels.json")));
    static GitHubIssueLabelWriter Writer(FakeWriteClient transport) => new(transport, new GitHubIssueReader(new FakeIssueReadClient(transport)));
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

    sealed class FakeIssueReadClient(FakeWriteClient writeClient) : IGitHubReadClient
    {
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            var labels = writeClient.Endpoint is null ? "[]" : "[{\"name\":\"existing-label\"}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"number\":42,\"title\":\"Issue\",\"state\":\"open\",\"html_url\":\"https://github.com/acme/widget/issues/42\",\"labels\":{labels}}}")
            });
        }
    }

    sealed class StubIssueReadClient(params string[] labels) : IGitHubReadClient
    {
        public int ReadCount { get; private set; }
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            var currentLabels = labels[Math.Min(ReadCount++, labels.Length - 1)];
            var labelObjects = currentLabels.Length == 0 ? Array.Empty<Dictionary<string, string>>() : currentLabels.Split(',').Select(name => new Dictionary<string, string> { ["name"] = name }).ToArray();
            var json = JsonSerializer.Serialize(new { number = 42, title = "Issue", state = "open", html_url = "https://github.com/acme/widget/issues/42", labels = labelObjects });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    sealed class ReadyWriteClient(HttpStatusCode status = HttpStatusCode.NoContent) : IGitHubWriteClient
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Endpoint { get; private set; }
        public Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
        {
            Method = method;
            Endpoint = endpoint;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    sealed class ReadyReadClient(params string[] labelSets) : IGitHubReadClient
    {
        int reads;
        public string[] LastLabels { get; private set; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            LastLabels = labelSets[Math.Min(reads++, labelSets.Length - 1)].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var labels = LastLabels.Select(name => new { name });
            var body = JsonSerializer.Serialize(new { number = 42, title = "Issue", state = "open", html_url = "https://github.com/acme/widget/issues/42", labels });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
