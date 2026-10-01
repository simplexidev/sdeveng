namespace SdevEng.Tests;

public class ProjectDiscoveryTests
{
    public const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    [Fact] public void FallsBackToWorkingDirectoryWhenSourceLinkPathIsUnavailable() { var root = AgentTool.FindToolkit(source: "/_/tools/AgentTool.cs"); Assert.True(File.Exists(Path.Combine(root, "config", "toolkit.json"))); }
    [Fact] public void IgnoresGeneratedAndSymlinkDirectories() { using var repo = new TemporaryGitRepository(); repo.Write("src/A.csproj", Project); repo.Write("obj/Generated.csproj", Project); Directory.CreateSymbolicLink(Path.Combine(repo.Root, "loop"), repo.Root); Assert.Single(Projects.Discover(repo.Root)); }
    [Fact] public async Task ReverseDependentsAreSelected() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project); repo.Write("A/A.cs", "class A {}"); repo.Write("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>"); repo.Write("C/C.csproj", Project); var result = await Projects.Affected(repo.Root, ["A/A.cs"]); Assert.Equal(2, result.Projects.Length); Assert.DoesNotContain(result.Projects, p => p.EndsWith("C.csproj", StringComparison.Ordinal)); }
    [Fact] public async Task SharedPropsWidenScope() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project); repo.Write("B/B.csproj", Project); Assert.Equal(2, (await Projects.Affected(repo.Root, ["Directory.Build.props"])).Projects.Length); }
    [Fact] public async Task MultiTargetedGraphWidensConservatively() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project.Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net9.0;net10.0</TargetFrameworks>", StringComparison.Ordinal)); repo.Write("A/a.cs", "class A {}"); repo.Write("B/B.csproj", Project); Assert.Equal(2, (await Projects.Affected(repo.Root, ["A/a.cs"])).Projects.Length); }
    [Fact] public async Task NoChangesMeansNoBuilds() { using var repo = new TemporaryGitRepository(); repo.Write("A.csproj", Project); Assert.Empty((await Projects.Affected(repo.Root, [])).Projects); }
    [Fact] public async Task DurableResultsDoNotSelectBuilds() { using var repo = new TemporaryGitRepository(); repo.Write("A.csproj", Project); Assert.Empty((await Projects.Affected(repo.Root, [".agent-results/reports/audit.md"])).Projects); }
    [Fact] public async Task LocateSkipsHistoricalResults() { using var repo = new TemporaryGitRepository(); repo.Write(".agent-results/reports/audit.md", "historical"); repo.Write("docs/audit.md", "current"); var result = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--query", "audit"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new())); var json = System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json); Assert.Contains("docs/audit.md", json, StringComparison.Ordinal); Assert.DoesNotContain(".agent-results", json, StringComparison.Ordinal); }
    [Fact]
    public async Task DescribeDiscoversRootAndListsTrackedFilesWithIgnoreRules()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/Tracked.cs", "class Tracked {}"); repo.Commit();
        repo.Write("obj/Generated.cs", "class Generated {}"); repo.Write("untracked.cs", "class Untracked {}");
        var result = await CommandTestRuntime.Execute(Cli.Parse(["repo", "describe"]), AgentTool.FindToolkit(), Path.Combine(repo.Root, "src"), new(new(), new(), new(), new()));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        Assert.Equal(Path.GetFullPath(repo.Root), json["root"]!.GetValue<string>());
        Assert.Contains("src/Tracked.cs", json["trackedFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("obj/Generated.cs", json["trackedFiles"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("untracked.cs", json["trackedFiles"]!.ToJsonString(), StringComparison.Ordinal);
    }
}
