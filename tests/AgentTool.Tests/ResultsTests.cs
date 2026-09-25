using System.Text.Json;

namespace CodexToolkit.Tests;

public class ResultsTests
{
    [Fact]
    public void InitIsIdempotentAndCreatesExpectedLayout()
    {
        using var repo = new TemporaryGitRepository();
        Results.Init(repo.Root); var readme = Path.Combine(repo.Root, ".agent-results", "README.md"); var original = File.ReadAllText(readme);
        Results.Init(repo.Root);
        Assert.Equal(original, File.ReadAllText(readme));
        foreach (var folder in new[] { "audits", "handoffs", "reviews", "reports", "evals", "logs", "traces", "sarif", "binlogs", "test-results", "tmp", "evals/generated" }) Assert.True(Directory.Exists(Path.Combine(repo.Root, ".agent-results", folder)));
    }

    [Fact]
    public async Task NewUsesUtcTimestampAndRejectsUnsafeValues()
    {
        using var repo = new TemporaryGitRepository();
        var result = await Results.New(repo.Root, ["handoff", "safe-name"]);
        var json = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!; var path = json["path"]!.GetValue<string>();
        Assert.Matches(@"\\handoffs\\|/handoffs/", path); Assert.Matches(@"\d{8}T\d{6}Z-safe-name\.md$", path);
        var text = File.ReadAllText(path); Assert.Contains("- Time (UTC):", text); Assert.Contains("- HEAD:", text); Assert.Contains("- Branch: main", text);
        await Assert.ThrowsAsync<ArgumentException>(() => Results.New(repo.Root, ["unknown", "safe-name"]));
        await Assert.ThrowsAsync<ArgumentException>(() => Results.New(repo.Root, ["handoff", "../escape"]));
    }

    [Fact]
    public void ListLatestAndContextAreOrderedAndCompact()
    {
        using var repo = new TemporaryGitRepository(); Results.Init(repo.Root); var folder = Path.Combine(repo.Root, ".agent-results", "handoffs");
        File.WriteAllText(Path.Combine(folder, "20250101T000000Z-old.md"), "- Status: done\n\n## Follow-up\n\nold next\n");
        File.WriteAllText(Path.Combine(folder, "20260101T000000Z-new.md"), "- Status: active\n\n## Unresolved\n\nneed review\n\n## Follow-up\n\nrun tests\n");
        var listed = JsonSerializer.SerializeToNode(Results.List(repo.Root, ["handoff"]).Data, AgentTool.Json)!;
        Assert.Equal(2, listed["count"]!.GetValue<int>()); Assert.EndsWith("20260101T000000Z-new.md", listed["results"]![0]!["path"]!.GetValue<string>());
        var latest = JsonSerializer.SerializeToNode(Results.Latest(repo.Root, ["handoff"]).Data, AgentTool.Json)!; Assert.EndsWith("new.md", latest["result"]!["path"]!.GetValue<string>());
        var context = JsonSerializer.SerializeToNode(Results.Context(repo.Root, ["handoff"]).Data, AgentTool.Json)!; Assert.Contains("need review", context["result"]!["carryForward"]!.GetValue<string>()); Assert.DoesNotContain("old next", context.ToJsonString());
    }

    [Fact]
    public void CleanPreservesDurableFilesAndHonorsDryRun()
    {
        using var repo = new TemporaryGitRepository(); Results.Init(repo.Root); var store = Path.Combine(repo.Root, ".agent-results");
        var durable = Path.Combine(store, "reports", "durable.md"); var transient = Path.Combine(store, "logs", "run.log"); File.WriteAllText(durable, "keep"); File.WriteAllText(transient, "remove");
        Assert.Equal(1, JsonSerializer.SerializeToNode(Results.Clean(repo.Root, true).Data, AgentTool.Json)!["removed"]!.GetValue<int>()); Assert.True(File.Exists(transient));
        Results.Clean(repo.Root, false); Assert.False(File.Exists(transient)); Assert.True(File.Exists(durable));
    }

    [Fact]
    public void CleanPreservesNonGeneratedEvaluationsAndRejectsLinkedTransientRoots()
    {
        using var repo = new TemporaryGitRepository(); Results.Init(repo.Root); var store = Path.Combine(repo.Root, ".agent-results");
        var evaluation = Path.Combine(store, "evals", "summary.md"); var generated = Path.Combine(store, "evals", "generated", "run.json"); File.WriteAllText(evaluation, "keep"); File.WriteAllText(generated, "remove");
        Results.Clean(repo.Root, false); Assert.True(File.Exists(evaluation)); Assert.False(File.Exists(generated));
        Directory.Delete(Path.Combine(store, "logs")); Directory.CreateSymbolicLink(Path.Combine(store, "logs"), Path.GetTempPath());
        Assert.Throws<IOException>(() => Results.Clean(repo.Root, true));
    }

    [Fact]
    public async Task GitIgnoreAndDiscoveryExcludeAgentResults()
    {
        using var repo = new TemporaryGitRepository();
        File.AppendAllText(Path.Combine(repo.Root, ".gitignore"), ".agent-results/\n"); Results.Init(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, ".agent-results", "reports", "kept.md"), "durable"); File.WriteAllText(Path.Combine(repo.Root, ".agent-results", "logs", "ignored.log"), "transient");
        var reportIgnored = await Processes.Run("git", ["check-ignore", ".agent-results/reports/kept.md"], repo.Root); Assert.Equal(0, reportIgnored.ExitCode);
        var ignored = await Processes.Run("git", ["check-ignore", ".agent-results/logs/ignored.log"], repo.Root); Assert.Equal(0, ignored.ExitCode);
        var files = await Git.Files(repo.Root); Assert.DoesNotContain(files, x => x.StartsWith(".agent-results/", StringComparison.Ordinal));
        Assert.DoesNotContain(SafeFiles.Enumerate(repo.Root), x => x.Contains(".agent-results", StringComparison.Ordinal));
    }
}
