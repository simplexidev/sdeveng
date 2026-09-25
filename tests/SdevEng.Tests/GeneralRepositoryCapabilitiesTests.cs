using System.IO.Compression;
using System.Text.Json;

namespace SdevEng.Tests;

public class GeneralRepositoryCapabilitiesTests
{
    [Fact]
    public void ActionsSummaryPreservesFailedJobAndStepEvidence()
    {
        var path = Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/github/actions-run.json");
        var node = JsonSerializer.SerializeToNode(GitHub.ParseActions(File.ReadAllText(path), false, new()), AgentTool.Json)!;
        Assert.Equal("github-actions-summary", node["kind"]!.GetValue<string>());
        Assert.Equal(1, node["failedJobs"]!.GetValue<int>());
        Assert.Equal(1, node["cancelledJobs"]!.GetValue<int>());
        Assert.Equal("Test", node["jobs"]![0]!["failedSteps"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConflictForecastDoesNotChangeRefsIndexOrWorktree()
    {
        using var repo = new TemporaryGitRepository(); repo.Write("shared.txt", "base\n"); repo.Commit(); repo.Run("branch", "other");
        repo.Write("shared.txt", "head\n"); repo.Commit(); var head = repo.Run("rev-parse", "HEAD").Trim();
        repo.Run("switch", "other"); repo.Write("shared.txt", "other\n"); repo.Commit(); repo.Run("switch", "main");
        var before = await Git.State(repo.Root); var node = JsonSerializer.SerializeToNode(await Git.ConflictForecast(repo.Root, "other", new()), AgentTool.Json)!; var after = await Git.State(repo.Root);
        Assert.True(node["hasConflicts"]!.GetValue<bool>()); Assert.Contains("shared.txt", node["paths"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(head, repo.Run("rev-parse", "HEAD").Trim()); Assert.Equal(before.Root, after.Root); Assert.Equal(before.Branch, after.Branch); Assert.Equal(before.Clean, after.Clean); Assert.Equal(before.Operations, after.Operations); Assert.Equal(before.Entries, after.Entries);
    }

    [Fact]
    public void SarifBaselineReportsOnlyNewFindings()
    {
        var root = AgentTool.FindToolkit(); var baseline = Path.Combine(root, "tests/fixtures/sarif/security.sarif"); var current = Path.Combine(root, "tests/fixtures/sarif/mixed.sarif");
        var node = JsonSerializer.SerializeToNode(Output.Sarif(current, new(), baseline), AgentTool.Json)!;
        Assert.Equal("sarif-summary", node["kind"]!.GetValue<string>()); Assert.Equal(1, node["baseline"]!["added"]!.GetValue<int>()); Assert.Equal(1, node["baseline"]!["unchanged"]!.GetValue<int>());
    }

    [Fact]
    public void ArtifactInspectionAndVerificationUseExactBytes()
    {
        using var repo = new TemporaryGitRepository(); var zip = Path.Combine(repo.Root, "drop.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { var entry = archive.CreateEntry("release/app.txt"); using var writer = new StreamWriter(entry.Open()); writer.Write("payload"); }
        var inspected = JsonSerializer.SerializeToNode(Artifacts.Inspect(zip, new()), AgentTool.Json)!; var hash = inspected["sha256"]!.GetValue<string>();
        Assert.True(inspected["safeToExtract"]!.GetValue<bool>()); Assert.Equal(0, Artifacts.Verify(zip, hash).ExitCode); Assert.Equal(1, Artifacts.Verify(zip, new string('0', 64)).ExitCode);
    }

    [Fact]
    public void ArtifactInspectionFlagsUnsafeZipPathsWithoutExtracting()
    {
        using var repo = new TemporaryGitRepository(); var zip = Path.Combine(repo.Root, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("../escape.txt");
        var node = JsonSerializer.SerializeToNode(Artifacts.Inspect(zip, new()), AgentTool.Json)!;
        Assert.False(node["safeToExtract"]!.GetValue<bool>()); Assert.Equal("path-traversal", node["issues"]![0]!["reason"]!.GetValue<string>()); Assert.False(File.Exists(Path.Combine(repo.Root, "escape.txt")));
    }

    [Fact]
    public async Task HygieneReportsCandidatesWithoutDeletingThem()
    {
        using var repo = new TemporaryGitRepository(); repo.Write("src/client.generated.js", "// AUTO-GENERATED. DO NOT EDIT.\n"); repo.Commit();
        var node = JsonSerializer.SerializeToNode(await Repository.Hygiene(repo.Root, new()), AgentTool.Json)!;
        Assert.Equal(1, node["candidateCount"]!.GetValue<int>()); Assert.Equal("generated-content-marker", node["candidates"]![0]!["reason"]!.GetValue<string>()); Assert.True(File.Exists(Path.Combine(repo.Root, "src/client.generated.js")));
    }
}
