using System.Text.Json.Nodes;
namespace SdevEng.Tests;

public class JevResponseTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/jev", name + "-response.json"));
    [Theory]
    [InlineData("choice")]
    [InlineData("score")]
    public async Task OfficialTypedShapesAreAccepted(string type)
    {
        using var repo = new TemporaryGitRepository(); using var handler = new FakeHttpMessageHandler(Fixture(type)); using var http = new HttpClient(handler);
        var criteria = JsonNode.Parse(type == "choice" ? "{\"build\":\"Build failure\",\"test\":\"Test failure\"}" : "[\"irrelevant\",\"possible\",\"relevant\"]");
        var result = await JevClientTests.Client(http, new(), Path.Combine(repo.Root, "cache")).Judge(JevClient.Request(type, "Public diagnostic summary", "Which category?", criteria, "fixture-only"));
        Assert.Equal("ACCEPT", result.Status);
    }
    [Fact]
    public async Task LowConfidenceCannotPass()
    {
        using var repo = new TemporaryGitRepository(); var response = JsonNode.Parse(Fixture("choice"))!; response["answers"]!["judgment"]!["confidence"] = .3;
        using var handler = new FakeHttpMessageHandler(response.ToJsonString()); using var http = new HttpClient(handler);
        var result = await JevClientTests.Client(http, new(), Path.Combine(repo.Root, "cache")).Judge(JevClient.Request("choice", "summary", "Category?", JsonNode.Parse("{\"build\":\"Build\",\"test\":\"Test\"}"), "fixture-only"));
        Assert.Equal("REVIEW", result.Status);
    }
    [Fact]
    public async Task OversizedInputAvoidsNetwork()
    {
        using var handler = new FakeHttpMessageHandler(JevClientTests.Good); using var http = new HttpClient(handler);
        var request = JevClient.Request("noul", new string('a', 20000), "Relevant?", null, "fixture-only");
        Assert.Equal("REVIEW", (await JevClientTests.Client(http, new(), "/unused").Judge(request)).Status); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task RequiredFailureHasNonzeroExitAndReviewFallback()
    {
        using var handler = new FakeHttpMessageHandler("") { Timeout = true }; using var http = new HttpClient(handler);
        var result = await JevClientTests.Client(http, new() { Mode = "required" }, "/unused").Judge(JevClientTests.Request());
        Assert.Equal(3, result.ExitCode); Assert.Equal("REVIEW", result.Status);
    }
    [Theory]
    [InlineData("choice")]
    [InlineData("score")]
    public async Task NullProbabilityReviewsAndInvalidCacheRefreshes(string type)
    {
        using var repo = new TemporaryGitRepository();
        var criteria = JsonNode.Parse(type == "choice" ? "{\"build\":\"Build failure\",\"test\":\"Test failure\"}" : "[\"irrelevant\",\"possible\",\"relevant\"]");
        var request = JevClient.Request(type, "summary", "Classify", criteria, "fixture-only");
        var response = type == "choice" ? "{\"answers\":{\"judgment\":{\"type\":\"choice\",\"choice\":\"test\",\"confidence\":1,\"probabilities\":{\"build\":null,\"test\":1}}}}" : "{\"answers\":{\"judgment\":{\"type\":\"score\",\"score\":2,\"confidence\":1,\"probabilities\":{\"0\":null,\"1\":0,\"2\":1},\"legend\":{\"0\":\"irrelevant\",\"1\":\"possible\",\"2\":\"relevant\"}}}}";
        using var handler = new FakeHttpMessageHandler(response); using var http = new HttpClient(handler);
        var cache = Path.Combine(repo.Root, "cache"); Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, JevClient.Hash(request, new JevSettings().ApiUrl) + ".json"), response);
        var result = await JevClientTests.Client(http, new(), cache).Judge(request);
        Assert.Equal("REVIEW", result.Status); Assert.Equal(1, handler.Calls);
        using var refreshHandler = new FakeHttpMessageHandler(Fixture(type)); using var refreshHttp = new HttpClient(refreshHandler);
        Assert.Equal("ACCEPT", (await JevClientTests.Client(refreshHttp, new(), cache).Judge(request)).Status); Assert.Equal(1, refreshHandler.Calls); Assert.DoesNotContain("null", File.ReadAllText(Directory.GetFiles(cache).Single()), StringComparison.Ordinal);
        using var requiredHandler = new FakeHttpMessageHandler(response); using var requiredHttp = new HttpClient(requiredHandler);
        Assert.Equal(3, (await JevClientTests.Client(requiredHttp, new() { Mode = "required" }, Path.Combine(repo.Root, "required")).Judge(request)).ExitCode);
    }
    [Fact]
    public async Task ExpiredCacheRefreshes()
    {
        using var repo = new TemporaryGitRepository(); var cache = Path.Combine(repo.Root, "cache"); Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, JevClient.Hash(JevClientTests.Request(), new JevSettings().ApiUrl) + ".json"); File.WriteAllText(path, JevClientTests.Good); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        using var handler = new FakeHttpMessageHandler(JevClientTests.Good); using var http = new HttpClient(handler);
        await JevClientTests.Client(http, new(), cache).Judge(JevClientTests.Request()); Assert.Equal(1, handler.Calls);
    }
}
