using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public class JevIntegrationValidationTests
{
    const string SyntheticKey = "jev-validation-synthetic-credential";

    [Theory]
    [InlineData("noul", null)]
    [InlineData("choice", "{\"build\":\"Build work\",\"docs\":\"Documentation work\"}")]
    [InlineData("score", "[\"irrelevant\",\"uncertain\",\"relevant\"]")]
    public void FormsDocumentedPrimitiveRequests(string kind, string? criteriaJson)
    {
        var criteria = criteriaJson is null ? null : JsonNode.Parse(criteriaJson);
        var request = JevClient.Request(kind, "Synthetic public state", "Classify this state", criteria, "fixture-only");

        Assert.Equal("fixture-only", request["model"]!.GetValue<string>());
        Assert.Equal("Synthetic public state", request["state"]!.GetValue<string>());
        var question = request["questions"]!["judgment"]!;
        Assert.Equal(kind, question["type"]!.GetValue<string>());
        Assert.Equal("Classify this state", question["instructions"]!.GetValue<string>());
        if (criteria is null) Assert.Null(question["criteria"]);
        else Assert.True(JsonNode.DeepEquals(criteria, question["criteria"]));
    }

    [Fact]
    public async Task ScreenDryRunFormsOneNoulRequestPerCandidate()
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "screen.json");
        File.WriteAllText(input, """
            {"capability":"relevance","purpose":"docs-impact","deterministicNarrowed":true,"query":"build documentation","candidates":[
              {"id":"README.md","text":"Build prerequisites and test commands."},
              {"id":"art.txt","text":"Public color palette notes."}
            ]}
            """);

        var result = await AgentTool.Execute(
            Cli.Parse(["jev", "screen", "--input", input, "--dry-run"]),
            AgentTool.FindToolkit(), repo.Root, Settings.Load(AgentTool.FindToolkit(), _ => null));
        var data = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        var rows = data["judgments"]!.AsArray();

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["README.md", "art.txt"], rows.Select(row => row!["id"]!.GetValue<string>()));
        Assert.All(rows, row =>
        {
            Assert.Equal("ok", row!["judgment"]!["status"]!.GetValue<string>());
            var request = row["judgment"]!["data"]!;
            Assert.Equal("noul", request["questions"]!["judgment"]!["type"]!.GetValue<string>());
            Assert.Equal("Is this candidate relevant to: build documentation", request["questions"]!["judgment"]!["instructions"]!.GetValue<string>());
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"query\":\"relevant?\"}")]
    [InlineData("{\"query\":\"relevant?\",\"candidates\":{}}")]
    [InlineData("{\"candidates\":[{\"id\":\"a\",\"text\":\"safe\"}]}")]
    [InlineData("{\"query\":\"relevant?\",\"candidates\":[{\"text\":\"safe\"}]}")]
    [InlineData("{\"query\":\"relevant?\",\"candidates\":[{\"id\":\"a\",\"text\":\"safe\"},{\"id\":\"a\",\"text\":\"other\"}]}")]
    public async Task MalformedScreenInputReviewsWithoutClassifying(string json)
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "screen.json");
        var parsed = JsonNode.Parse(json);
        if (parsed is JsonObject obj)
        {
            obj["capability"] = "relevance";
            obj["purpose"] = "candidate-relevance";
            obj["deterministicNarrowed"] = true;
            json = obj.ToJsonString();
        }
        File.WriteAllText(input, json);

        var result = await AgentTool.Execute(
            Cli.Parse(["jev", "screen", "--input", input, "--dry-run"]),
            AgentTool.FindToolkit(), repo.Root, Settings.Load(AgentTool.FindToolkit(), _ => null));

        Assert.Equal("REVIEW", result.Status);
        Assert.Contains("no candidate discarded", JsonSerializer.Serialize(result, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenCandidateLimitRequiresDeterministicNarrowing()
    {
        using var repo = new TemporaryGitRepository();
        var input = Path.Combine(repo.Root, "screen.json");
        File.WriteAllText(input, "{\"capability\":\"relevance\",\"purpose\":\"candidate-relevance\",\"deterministicNarrowed\":true,\"query\":\"relevant?\",\"candidates\":[{\"id\":\"a\",\"text\":\"safe\"},{\"id\":\"b\",\"text\":\"safe\"}]}");
        var loaded = Settings.Load(AgentTool.FindToolkit(), _ => null);
        var settings = loaded with { Jev = loaded.Jev with { MaxCandidates = 1 } };

        var result = await AgentTool.Execute(
            Cli.Parse(["jev", "screen", "--input", input, "--dry-run"]),
            AgentTool.FindToolkit(), repo.Root, settings);

        Assert.Equal("REVIEW", result.Status);
        Assert.Contains("narrow deterministic search first", JsonSerializer.Serialize(result, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenSavingFlowSendsOnlyIncludeAndReviewToCodex()
    {
        var discovered = new[]
        {
            new Candidate("README.md", true, .80),
            new Candidate("docs/build.md", true, .45),
            new Candidate("docs/colors.md", true, .05),
            new Candidate("logo.png", false, .99)
        };

        var narrowed = discovered.Where(candidate => candidate.DeterministicMatch).ToArray();
        var screened = narrowed.Select(candidate => new { candidate.Id, Route = JevClient.Route(candidate.Probability, new()) }).ToArray();
        var sentToCodex = screened.Where(candidate => candidate.Route is "INCLUDE" or "REVIEW").Select(candidate => candidate.Id).ToArray();

        Assert.Equal(["INCLUDE", "REVIEW", "EXCLUDE"], screened.Select(candidate => candidate.Route));
        Assert.Equal(["README.md", "docs/build.md"], sentToCodex);
        Assert.DoesNotContain(screened, candidate => candidate.Id == "logo.png");
    }

    [Theory]
    [InlineData("auto", HttpStatusCode.ServiceUnavailable, "{}", 0)]
    [InlineData("required", HttpStatusCode.ServiceUnavailable, "{}", 3)]
    [InlineData("auto", HttpStatusCode.OK, "not-json", 0)]
    [InlineData("required", HttpStatusCode.OK, "not-json", 3)]
    public async Task HttpAndMalformedResponsesConservativelyReview(string mode, HttpStatusCode statusCode, string response, int exitCode)
    {
        using var handler = new FakeHttpMessageHandler(response, statusCode);
        using var http = new HttpClient(handler);

        var result = await new JevClient(http, new() { Mode = mode }, "/unused", () => SyntheticKey).Judge(JevClientTests.Request());

        Assert.Equal("REVIEW", result.Status);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task ScoreResponseWithoutRequiredLegendReviews()
    {
        using var repo = new TemporaryGitRepository();
        const string response = "{\"answers\":{\"judgment\":{\"type\":\"score\",\"score\":1.5,\"confidence\":0.9,\"probabilities\":{\"0\":0,\"1\":0.5,\"2\":0.5}}}}";
        using var handler = new FakeHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        var request = JevClient.Request("score", "Synthetic public state", "Rate relevance", JsonNode.Parse("[\"no\",\"maybe\",\"yes\"]"), "fixture-only");

        var result = await new JevClient(http, new(), Path.Combine(repo.Root, "cache"), () => SyntheticKey).Judge(request);

        Assert.Equal("REVIEW", result.Status);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "cache")));
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("score")]
    public async Task TypedCacheRoundTripIsDeterministicAndPersistsOnlyNormalizedAnswer(string kind)
    {
        using var repo = new TemporaryGitRepository();
        var criteria = JsonNode.Parse(kind == "choice" ? "{\"build\":\"Build\",\"test\":\"Test\"}" : "[\"irrelevant\",\"possible\",\"relevant\"]");
        var request = JevClient.Request(kind, "Synthetic public state", "Classify", criteria, "fixture-only");
        var response = File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/jev", kind + "-response.json"));
        using var handler = new FakeHttpMessageHandler(response);
        using var http = new HttpClient(handler);
        var cache = Path.Combine(repo.Root, "cache");
        var client = new JevClient(http, new(), cache, () => SyntheticKey);

        var first = await client.Judge(request);
        var second = await client.Judge(request);
        var persisted = File.ReadAllText(Assert.Single(Directory.GetFiles(cache)));

        Assert.Equal("ACCEPT", first.Status);
        Assert.Equal("ACCEPT", second.Status);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(SyntheticKey, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic public state", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("Classify", persisted, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(persisted)!["model"]);
    }

    [Fact]
    public async Task DisabledCacheNeitherReadsNorWrites()
    {
        using var repo = new TemporaryGitRepository();
        var cache = Path.Combine(repo.Root, "cache");
        using var handler = new FakeHttpMessageHandler(JevClientTests.Good);
        using var http = new HttpClient(handler);
        var client = new JevClient(http, new() { CacheHours = 0 }, cache, () => SyntheticKey);

        await client.Judge(JevClientTests.Request());
        await client.Judge(JevClientTests.Request());

        Assert.Equal(2, handler.Calls);
        Assert.False(Directory.Exists(cache));
    }

    record Candidate(string Id, bool DeterministicMatch, double Probability);
}
