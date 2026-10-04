using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class RunEvidenceExpansionTests
{
    private const string Id = "evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void PublicServicePreservesLineEndingsUnicodeAndRunScopedIdentity(string separator)
    {
        using var repo = new TemporaryGitRepository();
        var item = Item(repo.Root, new string('x', 100_000) + separator + "界😀" + separator);
        var service = new RunEvidenceExpansion();
        foreach (var run in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            var request = Request(run);
            var first = service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 300, "caller-id");
            Assert.Equal(EvidenceExpansionStatus.Expanded, first.Result.Status);
            Assert.Equal("界😀", first.Result.Excerpt);
            Assert.Equal(7, first.Result.Utf8Bytes);
            var requestSchema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-expansion-request.schema.json"));
            Assert.True(requestSchema.Evaluate(JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1,
                packId = request.PackId,
                packRevision = request.PackRevision,
                evidenceId = request.EvidenceId,
                runId = request.RunId,
                role = request.Role,
                startLine = request.StartLine,
                endLine = request.EndLine,
                requestId = "caller-id"
            })).IsValid);
            Assert.True(service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 200, "caller-id").Replay);
            Assert.Single(ValidateEvents(repo.Root, run));
        }
    }

    [Fact]
    public void AcceptedAndSequentialRetriesUseOneReaderAndOneSchemaValidEvent()
    {
        using var repo = new TemporaryGitRepository();
        var run = Guid.NewGuid();
        var request = Request(run) with { RequestId = "req-1" };
        var item = Item(repo.Root, "first\nsecret excerpt\nlast");
        var calls = 0;
        var service = Counting(() => calls++);
        var first = service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 10);
        Assert.Equal(EvidenceExpansionStatus.Expanded, first.Result.Status);
        Assert.Equal("secret excerpt", first.Result.Excerpt);
        Assert.Equal(14, first.Result.Utf8Bytes);
        Assert.True(first.Recorded);
        Assert.True(first.ContentAvailable);
        var replay = service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 10);
        Assert.True(replay.Replay);
        Assert.True(replay.Recorded);
        Assert.False(replay.ContentAvailable);
        Assert.Null(replay.Result.Excerpt);
        Assert.Equal(first.Result.Utf8Bytes, replay.Result.Utf8Bytes);
        Assert.Equal(1, calls);
        var recorded = Assert.Single(ValidateEvents(repo.Root, run));
        Assert.Equal("rev", recorded.GetProperty("packRevision").GetString());
        Assert.Equal(item.SourceRevision, recorded.GetProperty("sourceRevision").GetString());
        Assert.Equal(first.Digest, recorded.GetProperty("requestDigest").GetString());
        Assert.DoesNotContain("secret excerpt", recorded.GetRawText());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("actualTokens").ValueKind);
        Assert.Equal("unavailable", recorded.GetProperty("tokenMethod").GetString());
        Assert.NotNull(LocalRunEventStore.Explain(Events(repo.Root), run));

        foreach (var changed in new[] { request with { EndLine = 3 }, request with { Role = "other" }, request with { PackRevision = "old" } })
        {
            var conflict = service.Expand(repo.Root, Events(repo.Root), run, changed, "pack", "rev", [item], 10);
            Assert.Equal(EvidenceExpansionRejection.ConflictingReplay, conflict.Result.Rejection);
            Assert.False(conflict.Recorded);
        }
        Assert.Equal(EvidenceExpansionRejection.ConflictingReplay,
            service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item with { SourceRevision = "other" }], 10).Result.Rejection);
        Assert.Equal(EvidenceExpansionRejection.ConflictingReplay,
            service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 9).Result.Rejection);
        Assert.Equal(1, calls);
        Assert.Single(ValidateEvents(repo.Root, run));
    }

    [Theory]
    [InlineData("nested", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.RecursiveExpansion, 0)]
    [InlineData("stale-pack", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.StalePack, 0)]
    [InlineData("unknown", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.UnknownEvidenceId, 0)]
    [InlineData("range", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.MalformedRequest, 0)]
    [InlineData("allowance", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.MalformedRequest, 0)]
    [InlineData("bad-id", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.MalformedRequest, 0)]
    [InlineData("bad-evidence", EvidenceExpansionStatus.Rejected, EvidenceExpansionRejection.UnknownEvidenceId, 0)]
    [InlineData("section", EvidenceExpansionStatus.Omitted, EvidenceExpansionRejection.None, 1)]
    [InlineData("source", EvidenceExpansionStatus.Omitted, EvidenceExpansionRejection.None, 1)]
    [InlineData("path", EvidenceExpansionStatus.Omitted, EvidenceExpansionRejection.None, 1)]
    [InlineData("bytes", EvidenceExpansionStatus.BudgetExceeded, EvidenceExpansionRejection.None, 1)]
    [InlineData("lines", EvidenceExpansionStatus.BudgetExceeded, EvidenceExpansionRejection.None, 1)]
    public void TerminalOutcomesAreRecordedAndReplayWithoutReading(string scenario, EvidenceExpansionStatus status,
        EvidenceExpansionRejection rejection, int expectedCalls)
    {
        using var repo = new TemporaryGitRepository();
        var run = Guid.NewGuid();
        var request = Request(run);
        var item = Item(repo.Root, scenario == "bytes" ? "first\n" + new string('界', 100_000) : "first\nsecond\nlast");
        var allowance = 300;
        request = scenario switch
        {
            "nested" => request with { ParentRequestId = "parent" },
            "stale-pack" => request with { PackRevision = "old" },
            "unknown" => request with { EvidenceId = "evidence:" + new string('b', 64) },
            "bad-evidence" => request with { EvidenceId = "bad" },
            "range" => request with { StartLine = 0 },
            "bad-id" => request with { RequestId = "../bad" },
            "section" => request with { StartLine = null, EndLine = null, Section = "heading" },
            "lines" => request with { StartLine = 1, EndLine = 201 },
            _ => request
        };
        if (scenario == "allowance") allowance = 0;
        if (scenario == "source") item = item with { SourceRevision = "stale" };
        if (scenario == "path") item = item with { LocationKey = "repo:../outside" };
        var calls = 0;
        var service = Counting(() => calls++);
        var result = service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], allowance);
        Assert.Equal(status, result.Result.Status);
        Assert.Equal(rejection, result.Result.Rejection);
        Assert.Null(result.Result.Excerpt);
        Assert.Equal(0, result.Result.Utf8Bytes);
        Assert.True(result.Recorded);
        if (scenario == "section") Assert.Equal("unsupported-section", result.Reason);
        Assert.Single(ValidateEvents(repo.Root, run));
        Assert.True(service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], allowance).Replay);
        Assert.Equal(expectedCalls, calls);
        Assert.Single(ValidateEvents(repo.Root, run));
    }

    [Fact]
    public void UnownedOrMalformedRunCannotReadOrPersistAndPersistenceFailureThrows()
    {
        using var repo = new TemporaryGitRepository();
        var run = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var item = Item(repo.Root, "first\nsecond");
        var calls = 0;
        var service = Counting(() => calls++);
        foreach (var request in new[] { Request(foreign), Request(run) with { RunId = "bad" } })
            Assert.Equal(EvidenceExpansionRejection.WrongRun,
                service.Expand(repo.Root, Events(repo.Root), run, request, "pack", "rev", [item], 10).Result.Rejection);
        Assert.Equal(EvidenceExpansionRejection.WrongRun,
            service.Expand(repo.Root, Events(repo.Root), Guid.Empty, Request(run), "pack", "rev", [item], 10).Result.Rejection);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Events(repo.Root)));
        var blockedRoot = Path.Combine(repo.Root, "blocked");
        File.WriteAllText(blockedRoot, "file prevents event directory creation");
        Assert.ThrowsAny<IOException>(() => service.Expand(repo.Root, blockedRoot, run, Request(run), "pack", "rev", [item], 10));
        Assert.Equal(1, calls);
    }

    private static RunEvidenceExpansion Counting(Action count) => new((root, request, pack, revision, items, allowance) =>
    {
        count();
        return RepositoryEvidenceExpander.Expand(root, request, pack, revision, items, allowance);
    });

    private static EvidenceExpansionRequest Request(Guid run) => new("pack", "rev", Id, run.ToString("D"), "reviewer", 2, 2);
    private static string Events(string root) => Path.Combine(root, "events");
    private static EvidenceExpansionItem Item(string root, string content)
    {
        File.WriteAllText(Path.Combine(root, "source.txt"), content, new UTF8Encoding(false));
        return new(Id, "repo:source.txt", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
    }

    private static IReadOnlyList<JsonElement> ValidateEvents(string root, Guid run)
    {
        var events = LocalRunEventStore.Read(Events(root), run);
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/run-event.schema.json"));
        foreach (var item in events) Assert.True(schema.Evaluate(JsonNode.Parse(item.GetRawText())).IsValid, item.GetRawText());
        return events;
    }
}
