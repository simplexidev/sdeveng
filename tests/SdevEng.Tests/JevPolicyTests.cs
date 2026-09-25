using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public class JevPolicyTests
{
    static Settings Loaded() => Settings.Load(AgentTool.FindToolkit(), _ => null);

    [Fact]
    public void ConfigurationSeparatesBoundedJudgmentFromForbiddenDecisions()
    {
        var policies = Loaded().Jev.Capabilities;

        Assert.All(new[] { "relevance", "pr-triage", "sarif-triage", "failure-classification", "upstream-classification", "ambiguous-routing" }, id =>
        {
            Assert.True(policies[id].Allowed);
            Assert.Equal(0, policies[id].ExpectedCalls);
            Assert.True(policies[id].DeterministicFirst);
            Assert.InRange(policies[id].MaxCalls, 1, 25);
        });
        Assert.All(new[] { "exact-repository-facts", "exact-commands", "authorization-security", "code-generation", "architecture", "open-ended-debugging" }, id =>
        {
            Assert.False(policies[id].Allowed);
            Assert.Equal(0, policies[id].MaxCalls);
        });
    }

    [Fact]
    public async Task DisallowedPolicyNeverCallsHttpAndEmitsPayloadFreeEscalation()
    {
        var policy = Loaded().Jev.Capabilities["authorization-security"];
        using var handler = new FakeHttpMessageHandler(JevClientTests.Good);
        using var http = new HttpClient(handler);

        var result = await JevClientTests.Client(http, Loaded().Jev, "/unused").Judge(JevClientTests.Request(), policy, "authorization", "authorization-security");
        var json = JsonSerializer.SerializeToNode(result, AgentTool.Json)!;

        Assert.Equal("REVIEW", result.Status);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("authorization-security", json["data"]!["instrumentation"]!["capability"]!.GetValue<string>());
        Assert.Equal(1, json["data"]!["instrumentation"]!["counts"]!["escalations"]!.GetValue<int>());
        Assert.False(json["data"]!["instrumentation"]!["payloadCaptured"]!.GetValue<bool>());
        Assert.DoesNotContain("A public filename", json.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfidenceAndCallCountsAreStructuredWithoutPayloads()
    {
        var settings = Loaded().Jev;
        var policy = settings.Capabilities["relevance"];
        using var repo = new TemporaryGitRepository();
        using var handler = new FakeHttpMessageHandler(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/jev/choice-response.json")));
        using var http = new HttpClient(handler);
        var request = JevClient.Request("choice", "A public filename", "Which category?", JsonNode.Parse("{\"build\":\"Build\",\"test\":\"Test\"}"), "fixture-only");

        var result = await JevClientTests.Client(http, settings, Path.Combine(repo.Root, "cache")).Judge(request, policy, "docs-impact", "relevance");
        var telemetry = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!["instrumentation"]!;

        Assert.Equal(1, telemetry["counts"]!["remoteCalls"]!.GetValue<int>());
        Assert.Equal(.95, telemetry["confidence"]!["reported"]!.GetValue<double>());
        Assert.Equal("docs-impact", telemetry["purpose"]!.GetValue<string>());
        Assert.Equal(0, telemetry["contextAvoidedBytes"]!.GetValue<int>());
        Assert.False(telemetry["payloadCaptured"]!.GetValue<bool>());
        Assert.DoesNotContain("A public filename", telemetry.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeRequiresDeterministicNarrowingBeforeDryRun()
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "input.json");
        File.WriteAllText(input, "{\"capability\":\"ambiguous-routing\",\"purpose\":\"capability-tie-break\",\"state\":\"public summary\",\"instructions\":\"Which route?\",\"criteria\":{\"a\":\"A\",\"b\":\"B\"}}");

        var result = await AgentTool.Execute(Cli.Parse(["jev", "choice", "--input", input, "--dry-run"]), AgentTool.FindToolkit(), repo.Root, Loaded());

        Assert.Equal("REVIEW", result.Status);
        Assert.Contains("Deterministic narrowing is required", JsonSerializer.Serialize(result, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityConfidenceCanBeStricterThanGlobalDefault()
    {
        var settings = Loaded().Jev;
        var policy = settings.Capabilities["pr-triage"];
        var response = JsonNode.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/jev/choice-response.json")))!;
        response["answers"]!["judgment"]!["confidence"] = .82;
        using var handler = new FakeHttpMessageHandler(response.ToJsonString());
        using var http = new HttpClient(handler);
        var request = JevClient.Request("choice", "sanitized comment", "Which category?", JsonNode.Parse("{\"build\":\"Build\",\"test\":\"Test\"}"), "fixture-only");

        var result = await JevClientTests.Client(http, settings, "/unused").Judge(request, policy, "review-comment-categorization", "pr-triage");

        Assert.Equal("REVIEW", result.Status);
        Assert.True(JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!["instrumentation"]!["confidence"]!["uncertain"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CapabilityCandidateBudgetIsEnforcedBeforeRequests()
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "screen.json");
        File.WriteAllText(input, "{\"capability\":\"ambiguous-routing\",\"purpose\":\"capability-tie-break\",\"deterministicNarrowed\":true,\"query\":\"route\",\"candidates\":[{\"id\":\"a\",\"text\":\"one\"},{\"id\":\"b\",\"text\":\"two\"}]}");

        var result = await AgentTool.Execute(Cli.Parse(["jev", "screen", "--input", input, "--dry-run"]), AgentTool.FindToolkit(), repo.Root, Loaded());

        Assert.Equal("REVIEW", result.Status);
        Assert.Contains("call budget", JsonSerializer.Serialize(result, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenReportsBudgetAndContextAvoidanceField()
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "screen.json");
        File.WriteAllText(input, "{\"capability\":\"relevance\",\"purpose\":\"candidate-relevance\",\"deterministicNarrowed\":true,\"query\":\"docs\",\"candidates\":[{\"id\":\"a\",\"text\":\"public summary\"}]}");

        var result = await AgentTool.Execute(Cli.Parse(["jev", "screen", "--input", input, "--dry-run"]), AgentTool.FindToolkit(), repo.Root, Loaded());
        var telemetry = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!["instrumentation"]!;

        Assert.Equal(25, telemetry["budget"]!["maxCalls"]!.GetValue<int>());
        Assert.Equal(0, telemetry["counts"]!["remoteCalls"]!.GetValue<int>());
        Assert.Equal(0, telemetry["contextAvoidedBytes"]!.GetValue<int>());
    }
}
