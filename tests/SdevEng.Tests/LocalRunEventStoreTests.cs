using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class LocalRunEventStoreTests
{
    [Fact]
    public void EventsSurviveReopenInSequenceAndMatchContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendTransition(directory, runId, null, "created");
            LocalRunEventStore.AppendExternalIdentifier(directory, runId, "github", "pull-request", "42");
            var events = LocalRunEventStore.Read(directory, runId);
            Assert.Equal(2, events.Count);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Equal(1, events[0].GetProperty("sequence").GetInt32());
            Assert.Equal(2, events[1].GetProperty("sequence").GetInt32());
            Assert.Equal("42", events[1].GetProperty("identifier").GetString());
            Assert.Empty(LocalRunEventStore.Read(directory, Guid.NewGuid()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RepositoryAndBranchIdentifiersSurviveReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendRepositoryIdentifier(directory, runId, "simplexidev/sdeveng");
            LocalRunEventStore.AppendBranchIdentifier(directory, runId, "factory/run-42");

            var events = LocalRunEventStore.Read(directory, runId);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.Equal(2, events.Count);
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Equal("git", events[0].GetProperty("externalSystem").GetString());
            Assert.Equal("repository", events[0].GetProperty("identifierType").GetString());
            Assert.Equal("simplexidev/sdeveng", events[0].GetProperty("identifier").GetString());
            Assert.Equal("branch", events[1].GetProperty("identifierType").GetString());
            Assert.Equal("factory/run-42", events[1].GetProperty("identifier").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
