namespace SdevEng.Tests;

public class CommandSafetyTests
{
    [Fact] public void UnsupportedDryRunCannotSilentlyMutate() => Assert.Throws<ArgumentException>(() => Cli.Parse(["git", "issue-start", "--issue", "42", "--branch", "fix", "--dry-run"]).ValidateCommand("git issue-start"));
    [Fact] public void UnknownOptionsCannotBeSilentlyIgnored() => Assert.Throws<ArgumentException>(() => Cli.Parse(["repo", "health", "--apply"]).ValidateCommand("repo health"));
    [Fact] public async Task FailedActionLogsRequireASpecificRun() => await Assert.ThrowsAsync<ArgumentException>(() => GitHub.Actions(AgentTool.FindToolkit(), Path.Combine(Path.GetTempPath(), "unused"), null, true, new()));
    [Fact]
    public void ZeroErrorCountIsNotAnError()
    {
        var path = Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/msbuild/build-success.txt");
        var result = System.Text.Json.JsonSerializer.SerializeToNode(Output.SummarizeFile(path, new()))!;
        Assert.Equal(0, result["errorLines"]!.GetValue<int>()); Assert.Equal(0, result["warningLines"]!.GetValue<int>());
    }
    [Fact]
    public void UpdateRepairsMissingLinkWithoutDuplicateOwnership()
    {
        using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); var toolkit = AgentTool.FindToolkit();
        Installer.Run(toolkit, home, null, "install", false, false); File.Delete(Path.Combine(home, ".codex/AGENTS.md"));
        Installer.Run(toolkit, home, null, "update", false, false);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(Path.Combine(home, ".codex/sdeveng-install.json")), AgentTool.Json)!;
        Assert.Equal(manifest.Entries.Count, manifest.Entries.Distinct().Count());
        Assert.NotNull(new FileInfo(Path.Combine(home, ".codex/AGENTS.md")).LinkTarget);
    }
}
