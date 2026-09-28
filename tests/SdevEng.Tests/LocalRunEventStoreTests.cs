using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class LocalRunEventStoreTests
{
    [Fact]
    public void StatusAndExplainSummarizeEventsWithoutChangingTheStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendTransition(directory, runId, null, "created");
            LocalRunEventStore.AppendTransition(directory, runId, "created", "running");
            LocalRunEventStore.AppendPullRequestIdentifier(directory, runId, "42");
            var before = Directory.GetFiles(Path.Combine(directory, runId.ToString("D"))).Order().ToArray();

            var status = System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Status(directory, runId), AgentTool.Json)!;
            var explanation = System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Explain(directory, runId), AgentTool.Json)!;

            Assert.Equal("running", status["state"]!.GetValue<string>());
            Assert.Equal(3, status["eventCount"]!.GetValue<int>());
            Assert.Equal("42", status["identifiers"]![0]!["value"]!.GetValue<string>());
            Assert.Equal(3, explanation["timeline"]!.AsArray().Count);
            Assert.Equal(before, Directory.GetFiles(Path.Combine(directory, runId.ToString("D"))).Order());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResumeAndCancelAppendValidatedLifecycleTransitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendTransition(directory, runId, null, "created");
            LocalRunEventStore.AppendTransition(directory, runId, "created", "paused");
            var resumed = LocalRunEventStore.Resume(directory, runId);
            Assert.Equal("running", resumed.GetProperty("toState").GetString());
            var cancelled = LocalRunEventStore.Cancel(directory, runId);
            Assert.Equal("cancelled", cancelled.GetProperty("toState").GetString());
            Assert.Equal("cancelled", System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Status(directory, runId), AgentTool.Json)!["state"]!.GetValue<string>());
            Assert.Throws<InvalidOperationException>(() => LocalRunEventStore.Resume(directory, runId));
            Assert.Throws<InvalidOperationException>(() => LocalRunEventStore.Cancel(directory, runId));
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            foreach (var item in LocalRunEventStore.Read(directory, runId)) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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

    [Fact]
    public void IssueAndPullRequestIdentifiersSurviveReopenAndMatchContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendIssueIdentifier(directory, runId, "17");
            LocalRunEventStore.AppendPullRequestIdentifier(directory, runId, "42");

            var events = LocalRunEventStore.Read(directory, runId);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.Equal(2, events.Count);
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Equal(new[] { "issue", "pull-request" }, events.Select(item => item.GetProperty("identifierType").GetString()));
            Assert.Equal(new[] { "17", "42" }, events.Select(item => item.GetProperty("identifier").GetString()));
            Assert.All(events, item => Assert.Equal("github", item.GetProperty("externalSystem").GetString()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void StepCommitAndCiRunIdentifiersSurviveReopenAndMatchContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendStepCommitIdentifier(directory, runId, "0123456789abcdef0123456789abcdef01234567");
            LocalRunEventStore.AppendCiRunIdentifier(directory, runId, "123456789");

            var events = LocalRunEventStore.Read(directory, runId);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.Equal(2, events.Count);
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Equal(new[] { "git", "github-actions" }, events.Select(item => item.GetProperty("externalSystem").GetString()));
            Assert.Equal(new[] { "step-commit", "ci-run" }, events.Select(item => item.GetProperty("identifierType").GetString()));
            Assert.Equal(new[] { "0123456789abcdef0123456789abcdef01234567", "123456789" }, events.Select(item => item.GetProperty("identifier").GetString()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":1,\"runId\":\"RUN_ID\",\"sequence\":1,\"occurredAt\":\"2026-09-28T12:00:00.0000000+00:00\",\"eventType\":\"unknown\"}")]
    [InlineData("{\"schemaVersion\":1,\"runId\":\"RUN_ID\",\"sequence\":1,\"occurredAt\":\"2026-09-28T12:00:00.0000000+00:00\",\"eventType\":\"state-transition\",\"fromState\":null,\"toState\":\"created\",\"extra\":1}")]
    public void ReadRejectsMalformedEventsAndAppendDoesNotExtendThem(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            var path = Path.Combine(directory, runId.ToString("D"));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "00000000000000000001.json"), content.Replace("RUN_ID", runId.ToString("D")));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.AppendTransition(directory, runId, null, "created"));
            Assert.Single(Directory.GetFiles(path, "*.json"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReadRejectsBrokenTransitionChain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendTransition(directory, runId, null, "created");
            LocalRunEventStore.AppendTransition(directory, runId, "created", "running");
            var path = Path.Combine(directory, runId.ToString("D"), "00000000000000000002.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"fromState\":\"created\"", "\"fromState\":\"wrong\""));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
