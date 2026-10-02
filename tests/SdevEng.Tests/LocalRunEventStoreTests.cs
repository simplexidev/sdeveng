using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public class LocalRunEventStoreTests
{
    [Fact]
    public void NewCommitIdentifiersAreCanonicalWhileLegacyEventsRemainReadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            var sha = new string('a', 40);
            var canonical = LocalRunEventStore.AppendCommitIdentifier(directory, runId, sha);
            Assert.Equal("commit", canonical.GetProperty("identifierType").GetString());
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendExternalIdentifier(directory, runId, "git", "step-commit", sha));
            var path = Directory.GetFiles(Path.Combine(directory, runId.ToString("D")), "*.json").Single();
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"identifierType\":\"commit\"", "\"identifierType\":\"step-commit\"", StringComparison.Ordinal));
            Assert.Equal("step-commit", LocalRunEventStore.Read(directory, runId).Single().GetProperty("identifierType").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailureEvidenceIsRedactedBoundedVersionedAndExplainOffersOnlyExplicitExpansion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            var sha = new string('a', 40);
            var evidence = LocalRunEventStore.AppendCiFailureEvidence(directory, runId, sha, "81", "91", "step-failure", false,
                "token=secret " + new string('x', 5000));
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.True(schema.Evaluate(JsonNode.Parse(evidence.GetRawText())!).IsValid);
            Assert.Equal(1, evidence.GetProperty("evidenceVersion").GetInt32());
            Assert.True(evidence.GetProperty("truncated").GetBoolean());
            Assert.DoesNotContain("secret", evidence.GetProperty("excerpt").GetString());
            Assert.True(evidence.GetProperty("excerpt").GetString()!.Length <= 4096);
            LocalRunEventStore.AppendCiFailureEvidence(directory, runId, sha, "82", "92", "test-failure", false, "latest excerpt");
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiFailureEvidence(directory, runId, "bad", "81", "91", "failure", false, "text"));
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiFailureEvidence(directory, runId, sha, "", "91", "failure", false, "text"));
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiFailureEvidence(directory, runId, sha, "81", "91", "failure", false, " "));

            var before = Directory.GetFiles(Path.Combine(directory, runId.ToString("D"))).Order().ToArray();
            var explanation = System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Explain(directory, runId), AgentTool.Json)!;
            var summary = explanation["ciFailureEvidence"]!;
            Assert.Equal(sha, summary["commitSha"]!.GetValue<string>());
            Assert.Equal("82", summary["providerRunId"]!.GetValue<string>());
            Assert.Equal("92", summary["providerJobId"]!.GetValue<string>());
            Assert.Equal("latest excerpt", summary["excerpt"]!.GetValue<string>());
            Assert.Equal("sdeveng github actions --run-id 82 --failed-logs", summary["expansionCommand"]!.GetValue<string>());
            Assert.Equal(before, Directory.GetFiles(Path.Combine(directory, runId.ToString("D"))).Order());

            var path = Path.Combine(directory, runId.ToString("D"), "00000000000000000002.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"evidenceVersion\": 1", "\"evidenceVersion\": 2"));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CiSnapshotsAppendValidateAndExplainOnlyTheLatestBoundedSummary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            var sha = "0123456789abcdef0123456789abcdef01234567";
            var observedAt = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
            var first = LocalRunEventStore.AppendCiCheckSnapshot(directory, runId, sha, observedAt,
                ["success", "cancelled", "skipped"], [("11", "completed")]);
            var second = LocalRunEventStore.AppendCiCheckSnapshot(directory, runId, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", observedAt,
                ["failure"], [("12", "in_progress")]);
            Assert.Equal(1, first.GetProperty("sequence").GetInt32());
            Assert.Equal(2, second.GetProperty("sequence").GetInt32());
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            foreach (var item in LocalRunEventStore.Read(directory, runId)) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            var explanation = System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.Explain(directory, runId), AgentTool.Json)!;
            var summary = explanation["ciSnapshot"]!;
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", summary["commitSha"]!.GetValue<string>());
            Assert.Equal(1, summary["checks"]!["failure"]!.GetValue<int>());
            Assert.Equal("12", summary["workflowRuns"]![0]!["id"]!.GetValue<string>());
            Assert.Null(explanation["raw"]);
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiCheckSnapshot(directory, runId, "bad", observedAt, [], []));
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiCheckSnapshot(directory, runId, sha, observedAt, ["future"], []));
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendCiCheckSnapshot(directory, runId, sha, observedAt,
                Enumerable.Repeat("success", 101).ToArray(), []));
            var path = Path.Combine(directory, runId.ToString("D"), "00000000000000000002.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"snapshotVersion\": 1", "\"snapshotVersion\": 2"));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProgressEventsAreVersionedBoundedRedactedAndValidated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            foreach (var operation in new[] { "branch-created", "branch-pushed", "bootstrap-created", "pr-created", "pr-linked", "metadata-persisted" })
                foreach (var status in new[] { "completed", "retryable-failure", "terminal-failure" })
                {
                    var item = LocalRunEventStore.AppendStartWorkProgress(directory, runId, operation, status, "token=secret " + new string('x', 600));
                    Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
                    Assert.Equal(1, item.GetProperty("progressVersion").GetInt32());
                    Assert.DoesNotContain("secret", item.GetProperty("detail").GetString());
                    Assert.True(item.GetProperty("detail").GetString()!.Length <= 512);
                }
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendStartWorkProgress(directory, runId, "other", "completed"));
            Assert.Throws<ArgumentException>(() => LocalRunEventStore.AppendStartWorkProgress(directory, runId, "branch-created", "unknown"));
            var path = Path.Combine(directory, runId.ToString("D"), "00000000000000000001.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"progressVersion\":1", "\"progressVersion\":2"));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ListOrdersRunsByOrdinalRunId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var later = Guid.Parse("f0000000-0000-4000-8000-000000000000");
            var earlier = Guid.Parse("10000000-0000-4000-8000-000000000000");
            LocalRunEventStore.AppendTransition(directory, later, null, "created");
            LocalRunEventStore.AppendTransition(directory, earlier, null, "created");

            var result = System.Text.Json.JsonSerializer.SerializeToNode(LocalRunEventStore.List(directory), AgentTool.Json)!;

            Assert.Equal(new[] { earlier.ToString("D"), later.ToString("D") }, result["runs"]!.AsArray().Select(run => run!["runId"]!.GetValue<string>()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AbandonTransitionsOnlyExistingNonterminalRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendTransition(directory, runId, null, "running");
            Assert.Equal("abandoned", LocalRunEventStore.Abandon(directory, runId).GetProperty("toState").GetString());
            Assert.Throws<InvalidOperationException>(() => LocalRunEventStore.Abandon(directory, runId));
            Assert.Throws<InvalidOperationException>(() => LocalRunEventStore.Abandon(directory, Guid.NewGuid()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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
    public void ConfirmedPullRequestIdentityIsCanonicalIdempotentAndRejectsConflicts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendRepositoryIdentifier(directory, runId, "simplexidev/sdeveng");
            LocalRunEventStore.AppendConfirmedPullRequestIdentity(directory, runId, "simplexidev/sdeveng", 42);
            var count = LocalRunEventStore.Read(directory, runId).Count;
            LocalRunEventStore.AppendConfirmedPullRequestIdentity(directory, runId, "simplexidev/sdeveng", 42);
            var events = LocalRunEventStore.Read(directory, runId);
            Assert.Equal(count, events.Count);
            Assert.Equal(new[] { "repository", "pull-request-number", "pull-request-url" }, events.Select(item => item.GetProperty("identifierType").GetString()));
            Assert.Equal(new[] { "simplexidev/sdeveng", "42", "https://github.com/simplexidev/sdeveng/pull/42" }, events.Select(item => item.GetProperty("identifier").GetString()));
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.AppendConfirmedPullRequestIdentity(directory, runId, "simplexidev/sdeveng", 43));
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.AppendConfirmedPullRequestIdentity(directory, runId, "simplexidev/other", 42));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("https://github.com/simplexidev/sdeveng/pull/0")]
    [InlineData("http://github.com/simplexidev/sdeveng/pull/42")]
    [InlineData("https://github.com/other/sdeveng/pull/42")]
    [InlineData("https://github.com/simplexidev/sdeveng/pull/42?x=1")]
    public void ReadRejectsMalformedOrWrongRepositoryPullRequestUrl(string url)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runId = Guid.NewGuid();
            LocalRunEventStore.AppendRepositoryIdentifier(directory, runId, "simplexidev/sdeveng");
            LocalRunEventStore.AppendExternalIdentifier(directory, runId, "github", "pull-request-url", url);
            Assert.Throws<InvalidDataException>(() => LocalRunEventStore.Read(directory, runId));
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
            LocalRunEventStore.AppendCommitIdentifier(directory, runId, "0123456789abcdef0123456789abcdef01234567");
            LocalRunEventStore.AppendCiRunIdentifier(directory, runId, "123456789");

            var events = LocalRunEventStore.Read(directory, runId);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
            Assert.Equal(2, events.Count);
            foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())!).IsValid);
            Assert.Equal(new[] { "git", "github-actions" }, events.Select(item => item.GetProperty("externalSystem").GetString()));
            Assert.Equal(new[] { "commit", "ci-run" }, events.Select(item => item.GetProperty("identifierType").GetString()));
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
