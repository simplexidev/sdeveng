using System.Net;
using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public class JevClientTests
{
    public const string Good = "{\"answers\":{\"judgment\":{\"type\":\"noul\",\"noul\":0.9}}}";
    public static JsonObject Request() => JevClient.Request("noul", "A public filename", "Relevant to tests?", null, "jev-latest");
    internal static JevClient Client(HttpClient http, JevSettings settings, string cache, string? key = "test-only-value") => new(http, settings, cache, () => key);
    [Fact] public async Task FormsAuthorizedTypedRequest() { using var repo = new TemporaryGitRepository(); using var handler = new FakeHttpMessageHandler(Good); using var http = new HttpClient(handler); var client = Client(http, new(), Path.Combine(repo.Root, ".agent-tool/cache")); var r = await client.Judge(Request()); Assert.Equal("INCLUDE", r.Status); Assert.Equal("Bearer", handler.Authorization); Assert.Equal("test-only-value", handler.AuthorizationParameter); Assert.Equal("noul", JsonNode.Parse(handler.Body!)!["questions"]!["judgment"]!["type"]!.GetValue<string>()); }
    [Theory]
    [InlineData("auto", null, 0)]
    [InlineData("off", "test-only-value", 0)]
    [InlineData("required", null, 3)]
    public async Task MissingKeyOrDisabledNeverCallsHttp(string mode, string? key, int exit) { using var handler = new FakeHttpMessageHandler(Good); using var http = new HttpClient(handler); var r = await Client(http, new() { Mode = mode }, "/unused", key).Judge(Request()); Assert.Equal("REVIEW", r.Status); Assert.Equal(exit, r.ExitCode); Assert.Equal(0, handler.Calls); }
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"answers\":{\"judgment\":{\"type\":\"noul\",\"noul\":1.5}}}")]
    [InlineData("{\"answers\":{\"judgment\":{\"type\":\"choice\"}}}")]
    public async Task InvalidResponseFailsOpen(string response) { using var repo = new TemporaryGitRepository(); using var handler = new FakeHttpMessageHandler(response); using var http = new HttpClient(handler); Assert.Equal("REVIEW", (await Client(http, new(), Path.Combine(repo.Root, "cache")).Judge(Request())).Status); }
    [Fact] public async Task HttpFailureNeverExposesBody() { using var handler = new FakeHttpMessageHandler("private server failure", HttpStatusCode.Unauthorized); using var http = new HttpClient(handler); var r = await Client(http, new(), "/unused").Judge(Request()); Assert.Equal("REVIEW", r.Status); Assert.DoesNotContain("private server", System.Text.Json.JsonSerializer.Serialize(r)); }
    [Theory][InlineData(true)][InlineData(false)] public async Task TransportFailuresFailOpen(bool timeout) { using var handler = new FakeHttpMessageHandler("") { Timeout = timeout, Fail = !timeout }; using var http = new HttpClient(handler); Assert.Equal("REVIEW", (await Client(http, new(), "/unused").Judge(Request())).Status); }
    [Fact] public async Task SensitiveInputNeverSent() { using var handler = new FakeHttpMessageHandler(Good); using var http = new HttpClient(handler); var request = JevClient.Request("noul", "password=do-not-send", "Relevant?", null, "jev-latest"); Assert.Equal("REVIEW", (await Client(http, new(), "/unused").Judge(request)).Status); Assert.Equal(0, handler.Calls); }
    [Fact] public void ChoiceRejectsUndeclaredAnswer() { using var http = new HttpClient(new FakeHttpMessageHandler("")); var client = Client(http, new(), "/unused", null); var request = JevClient.Request("choice", "text", "category?", JsonNode.Parse("{\"a\":\"A\",\"b\":\"B\"}"), "jev-latest"); Assert.Throws<System.Text.Json.JsonException>(() => client.Parse(JsonNode.Parse("{\"answers\":{\"judgment\":{\"type\":\"choice\",\"choice\":\"c\",\"confidence\":1,\"probabilities\":{\"a\":1,\"b\":0}}}}")!, request, false)); }
}
