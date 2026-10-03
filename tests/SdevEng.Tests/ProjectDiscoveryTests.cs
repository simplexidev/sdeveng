using Json.Schema;

namespace SdevEng.Tests;

public class ProjectDiscoveryTests
{
    public const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    [Fact] public void FallsBackToWorkingDirectoryWhenSourceLinkPathIsUnavailable() { var root = AgentTool.FindToolkit(source: "/_/tools/AgentTool.cs"); Assert.True(File.Exists(Path.Combine(root, "config", "toolkit.json"))); }
    [Fact] public void IgnoresGeneratedAndSymlinkDirectories() { using var repo = new TemporaryGitRepository(); repo.Write("src/A.csproj", Project); repo.Write("obj/Generated.csproj", Project); Directory.CreateSymbolicLink(Path.Combine(repo.Root, "loop"), repo.Root); Assert.Single(Projects.Discover(repo.Root)); }
    [Fact] public async Task ReverseDependentsAreSelected() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project); repo.Write("A/A.cs", "class A {}"); repo.Write("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>"); repo.Write("C/C.csproj", Project); var result = await Projects.Affected(repo.Root, ["A/A.cs"]); Assert.Equal(2, result.Projects.Length); Assert.DoesNotContain(result.Projects, p => p.EndsWith("C.csproj", StringComparison.Ordinal)); }
    [Fact] public async Task DependencyGraphIsCanonicalAndTraversesTransitiveDependents() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project); repo.Write("B/B.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal)); repo.Write("C/C.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal)); var graph = await Projects.DependencyGraph(repo.Root); Assert.Equal(3, graph.Nodes.Length); Assert.Equal(2, graph.Edges.Length); Assert.Equal(new[] { Path.Combine(repo.Root, "B", "B.csproj"), Path.Combine(repo.Root, "C", "C.csproj") }, Projects.Dependents(graph, Path.Combine(repo.Root, "A", "A.csproj"))); }
    [Fact] public async Task SharedPropsWidenScope() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project); repo.Write("B/B.csproj", Project); Assert.Equal(2, (await Projects.Affected(repo.Root, ["Directory.Build.props"])).Projects.Length); }
    [Fact] public async Task MultiTargetedGraphWidensConservatively() { using var repo = new TemporaryGitRepository(); repo.Write("A/A.csproj", Project.Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net9.0;net10.0</TargetFrameworks>", StringComparison.Ordinal)); repo.Write("A/a.cs", "class A {}"); repo.Write("B/B.csproj", Project); Assert.Equal(2, (await Projects.Affected(repo.Root, ["A/a.cs"])).Projects.Length); }
    [Fact] public async Task NoChangesMeansNoBuilds() { using var repo = new TemporaryGitRepository(); repo.Write("A.csproj", Project); Assert.Empty((await Projects.Affected(repo.Root, [])).Projects); }
    [Fact] public async Task DurableResultsDoNotSelectBuilds() { using var repo = new TemporaryGitRepository(); repo.Write("A.csproj", Project); Assert.Empty((await Projects.Affected(repo.Root, [".agent-results/reports/audit.md"])).Projects); }
    [Fact] public async Task LocateSkipsHistoricalResults() { using var repo = new TemporaryGitRepository(); repo.Write(".agent-results/reports/audit.md", "historical"); repo.Write("docs/audit.md", "current"); var result = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--query", "audit"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new())); var json = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!; Assert.Contains("docs/audit.md", json.ToJsonString(), StringComparison.Ordinal); Assert.Equal("valid", json["indexIntegrity"]!.GetValue<string>()); Assert.DoesNotContain(".agent-results", json.ToJsonString(), StringComparison.Ordinal); }
    [Fact]
    public async Task FileCatalogIndexesSortedPathsNamesAndBoundedTextTerms()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("z/Needle.cs", "secret body text"); repo.Write("a/other.txt", "Needle in body");
        var catalog = await Repository.FileCatalog(repo.Root);
        Assert.Equal(new[] { ".gitignore", "a/other.txt", "z/Needle.cs" }, catalog.Files.Select(file => file.Path));
        Assert.Equal(new[] { ".gitignore", "other.txt", "Needle.cs" }, catalog.Files.Select(file => file.Name));
        Assert.Equal(1, catalog.IndexVersion);
        Assert.True(catalog.HasValidIntegrity());
        Assert.Equal(new[] { "a/other.txt", "z/Needle.cs" }, catalog.Find("needle").Select(file => file.Path));
        Assert.Equal(new[] { "a/other.txt", "z/Needle.cs" }, catalog.Find("body").Select(file => file.Path));
        Assert.Equal(new[] { "z/Needle.cs" }, catalog.FindExactPath("z\\Needle.cs").Select(file => file.Path));
        Assert.Empty(catalog.FindExactPath("Needle.cs"));
        Assert.Equal(new[] { "z/Needle.cs" }, catalog.FindName("needle").Select(file => file.Path));
        Assert.Equal("z/Needle.cs", catalog.SearchExactText("Needle", 10)[0].Path);
        Assert.Equal("a/other.txt", catalog.SearchFuzzyTerms("bodi", 10)[0].Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.SearchFuzzyTerms("body", 201));
    }

    [Fact]
    public async Task LocateExactTextAndFuzzyReturnBoundedRankedCandidates()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("z/Needle.cs", "needle uniquealpha");
        repo.Write("a/other.txt", "uniquealpha bodi");
        var exact = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--exact-text", "uniquealpha"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var exactJson = System.Text.Json.JsonSerializer.SerializeToNode(exact.Data, AgentTool.Json)!;
        Assert.Equal("a/other.txt", exactJson["candidates"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("exact", exactJson["candidates"]![0]!["match"]!.GetValue<string>());
        var fuzzy = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--fuzzy", "body"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var fuzzyJson = System.Text.Json.JsonSerializer.SerializeToNode(fuzzy.Data, AgentTool.Json)!;
        Assert.Equal("a/other.txt", fuzzyJson["candidates"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("fuzzy-term", fuzzyJson["candidates"]![0]!["match"]!.GetValue<string>());
    }

    [Fact]
    public async Task LocateSupportsExactPathAndNameOnlySearchThroughCli()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/Needle.cs", "body with a different term");
        repo.Write("docs/guide.md", "Needle appears only in this body");

        var exact = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--path", "src/Needle.cs"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var exactJson = System.Text.Json.JsonSerializer.SerializeToNode(exact.Data, AgentTool.Json)!;
        Assert.Equal(new[] { "src/Needle.cs" }, exactJson["matches"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(1, exactJson["total"]!.GetValue<int>());

        var byName = await CommandTestRuntime.Execute(Cli.Parse(["repo", "locate", "--name", "needle"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var nameJson = System.Text.Json.JsonSerializer.SerializeToNode(byName.Data, AgentTool.Json)!;
        Assert.Equal(new[] { "src/Needle.cs" }, nameJson["matches"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(1, nameJson["total"]!.GetValue<int>());
    }
    [Fact]
    public void FileCatalogIntegrityRejectsUnsortedDuplicateAndUnboundedEntries()
    {
        Assert.False(new RepositoryFileCatalog(1, [new("z.cs", "z.cs", []), new("a.cs", "a.cs", [])]).HasValidIntegrity());
        Assert.False(new RepositoryFileCatalog(1, [new("a.cs", "wrong.cs", [])]).HasValidIntegrity());
        Assert.False(new RepositoryFileCatalog(2, [new("a.cs", "a.cs", [])]).HasValidIntegrity());
        Assert.False(new RepositoryFileCatalog(1, [new("a.cs", "a.cs", Enumerable.Range(0, 257).Select(index => index.ToString()).ToArray())]).HasValidIntegrity());
    }
    [Fact]
    public async Task DescribeDiscoversRootAndListsTrackedFilesWithIgnoreRules()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/Tracked.cs", "class Tracked {}");
        repo.Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\"}}");
        repo.Write("Directory.Build.props", "<Project />");
        repo.Write("src/local.json", "{}");
        repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../lib/Library.vbproj\" /><PackageReference Include=\"Example.Package\" Version=\"2.3.4\" /></ItemGroup></Project>");
        repo.Write("tests/Tests.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("lib/Library.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Commit();
        repo.Write("obj/Generated.cs", "class Generated {}"); repo.Write("untracked.cs", "class Untracked {}");
        var result = await CommandTestRuntime.Execute(Cli.Parse(["repo", "describe"]), AgentTool.FindToolkit(), Path.Combine(repo.Root, "src"), new(new(), new(), new(), new()));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json"));
        Assert.True(schema.Evaluate(json, new() { OutputFormat = OutputFormat.List }).IsValid);
        Assert.Equal(Path.GetFullPath(repo.Root), json["root"]!.GetValue<string>());
        Assert.Contains("src/Tracked.cs", json["trackedFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "Directory.Build.props", "global.json" }, json["repositoryConfigurationFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(64, json["catalogFingerprint"]!.GetValue<string>().Length);
        var repeated = await CommandTestRuntime.Execute(Cli.Parse(["repo", "describe"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        Assert.Equal(json["catalogFingerprint"]!.GetValue<string>(), System.Text.Json.JsonSerializer.SerializeToNode(repeated.Data, AgentTool.Json)!["catalogFingerprint"]!.GetValue<string>());
        Assert.DoesNotContain("obj/Generated.cs", json["trackedFiles"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("untracked.cs", json["trackedFiles"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(3, json["projectCount"]!.GetValue<int>());
        Assert.Equal(new[] { "lib/Library.vbproj", "src/App.csproj", "tests/Tests.fsproj" }, json["graph"]!["nodes"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Single(json["graph"]!["edges"]!.AsArray());
        var projects = json["projects"]!.AsArray().ToDictionary(x => x!["path"]!.GetValue<string>());
        Assert.Equal(("C#", "application"), (projects["src/App.csproj"]!["language"]!.GetValue<string>(), projects["src/App.csproj"]!["kind"]!.GetValue<string>()));
        Assert.Equal(("F#", "test"), (projects["tests/Tests.fsproj"]!["language"]!.GetValue<string>(), projects["tests/Tests.fsproj"]!["kind"]!.GetValue<string>()));
        Assert.Equal(("Visual Basic", "library"), (projects["lib/Library.vbproj"]!["language"]!.GetValue<string>(), projects["lib/Library.vbproj"]!["kind"]!.GetValue<string>()));
        Assert.Equal(new[] { "lib/Library.vbproj" }, projects["src/App.csproj"]!["projectReferences"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(("Example.Package", "2.3.4"), (projects["src/App.csproj"]!["packageReferences"]![0]!["id"]!.GetValue<string>(), projects["src/App.csproj"]!["packageReferences"]![0]!["version"]!.GetValue<string>()));
        Assert.Equal(new[] { "net10.0" }, projects["src/App.csproj"]!["targetFrameworks"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("src/Tracked.cs", projects["src/App.csproj"]!["ownedFiles"]!.AsArray().Select(x => x!.GetValue<string>()));
    }
}
