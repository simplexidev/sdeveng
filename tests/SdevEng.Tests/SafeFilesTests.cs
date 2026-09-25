namespace SdevEng.Tests;

public class SafeFilesTests
{
    [Fact]
    public void AtomicRefusesReportSymlinkAndReplacesOrdinaryReport()
    {
        using var repo = new TemporaryGitRepository();
        var sentinel = Path.Combine(repo.Root, "sentinel"); File.WriteAllText(sentinel, "user-owned sentinel");
        var report = Path.Combine(repo.Root, ".agent-tool", "upstream-drift.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.CreateSymbolicLink(report, sentinel);
        Assert.Throws<IOException>(() => SafeFiles.Atomic(report, "[]"));
        Assert.Equal("user-owned sentinel", File.ReadAllText(sentinel));
        File.Delete(report); SafeFiles.Atomic(report, "[]"); Assert.Equal("[]", File.ReadAllText(report));
    }

    [Fact]
    public async Task UpstreamUpdateRefusesSymlinkedReportWithoutTouchingTarget()
    {
        using var repo = new TemporaryGitRepository();
        var toolkit = Path.Combine(repo.Root, "toolkit");
        Copy(AgentTool.FindToolkit(), toolkit);
        File.WriteAllText(Path.Combine(toolkit, "upstream", "versions.json"), "{\"repositories\":[]}");
        var artifacts = Path.Combine(repo.Root, ".agent-tool"); Directory.CreateDirectory(artifacts);
        var sentinel = Path.Combine(repo.Root, "sentinel"); File.WriteAllText(sentinel, "user-owned sentinel");
        File.CreateSymbolicLink(Path.Combine(artifacts, "upstream-drift.json"), sentinel);

        await Assert.ThrowsAsync<IOException>(() => AgentTool.Execute(Cli.Parse(["upstream", "update"]), toolkit, repo.Root, Settings.Load(toolkit)));
        Assert.Equal("user-owned sentinel", File.ReadAllText(sentinel));
    }

    static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in SafeFiles.Enumerate(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
