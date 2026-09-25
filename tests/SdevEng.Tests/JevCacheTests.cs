using System.Text.Json.Nodes;
namespace SdevEng.Tests;

public class JevCacheTests
{
    [Fact] public void HashCanonicalizesObjectOrderButNotRubricOrder() { var a = JsonNode.Parse("{\"a\":1,\"b\":2}")!; var b = JsonNode.Parse("{\"b\":2,\"a\":1}")!; Assert.Equal(JevClient.Hash(a, "https://example.invalid"), JevClient.Hash(b, "https://example.invalid")); Assert.NotEqual(JevClient.Hash(a, "https://other.invalid"), JevClient.Hash(a, "https://example.invalid")); Assert.NotEqual(JevClient.Hash(JsonNode.Parse("[1,2]")!, "x"), JevClient.Hash(JsonNode.Parse("[2,1]")!, "x")); }
    [Fact] public async Task CacheAvoidsHttpAndContainsNoKey() { using var repo = new TemporaryGitRepository(); using var handler = new FakeHttpMessageHandler(JevClientTests.Good); using var http = new HttpClient(handler); var dir = Path.Combine(repo.Root, "cache"); var client = JevClientTests.Client(http, new(), dir); await client.Judge(JevClientTests.Request()); var second = await client.Judge(JevClientTests.Request()); Assert.Equal("INCLUDE", second.Status); Assert.Equal(1, handler.Calls); Assert.DoesNotContain("test-only-value", File.ReadAllText(Assert.Single(Directory.GetFiles(dir)))); }
    [Fact] public async Task CorruptCacheIsNotAClassification() { using var repo = new TemporaryGitRepository(); var dir = Path.Combine(repo.Root, "cache"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, JevClient.Hash(JevClientTests.Request(), new JevSettings().ApiUrl) + ".json"), "bad"); using var handler = new FakeHttpMessageHandler(JevClientTests.Good); using var http = new HttpClient(handler); Assert.Equal("INCLUDE", (await JevClientTests.Client(http, new(), dir).Judge(JevClientTests.Request())).Status); Assert.Equal(1, handler.Calls); }
}
