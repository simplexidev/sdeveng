using System.Text.Json;

namespace SdevEng.Tests;

public class DeterministicPrimitivesTests
{
    const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    [Fact]
    public async Task RepositorySummaryIsBoundedAndClassified()
    {
        using var repo = new TemporaryGitRepository(); repo.Write("src/A.cs", "class A {}"); repo.Write("docs/note.md", "note");
        var node = JsonSerializer.SerializeToNode(await Repository.Summary(repo.Root, null, new() { MaxItems = 1 }), AgentTool.Json)!;
        Assert.Equal("repository-summary", node["kind"]!.GetValue<string>());
        Assert.Equal(2, node["changes"]!["count"]!.GetValue<int>());
        Assert.True(node["changes"]!["truncated"]!.GetValue<bool>());
        Assert.Equal(1, node["changes"]!["byExtension"]![".cs"]!.GetValue<int>());
    }

    [Fact]
    public async Task OwnershipUsesCompileGraphAndIncludesTransitiveImpact()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("A/A.csproj", Project); repo.Write("A/A.cs", "class A {}");
        repo.Write("B/B.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));
        repo.Write("C/C.csproj", Project.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));
        var node = JsonSerializer.SerializeToNode(await Projects.Ownership(repo.Root, "A/A.cs"), AgentTool.Json)!;
        Assert.Equal("evaluated-compile-item", node["basis"]!.GetValue<string>());
        Assert.Equal(3, node["impactedProjects"]!.AsArray().Count);
    }

    [Fact]
    public async Task InspectionNormalizesProjectGraphToRepositoryPaths()
    {
        using var repo = new TemporaryGitRepository(); repo.Write("src/A.csproj", Project);
        var node = JsonSerializer.SerializeToNode(await DotnetFacts.Inspect(repo.Root, null), AgentTool.Json)!;
        Assert.Equal("src/A.csproj", node["projects"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("net10.0", node["projects"]![0]!["targetFrameworks"]![0]!.GetValue<string>());
        Assert.NotNull(node["environment"]!["sdk"]);
    }

    [Fact]
    public async Task SolutionInspectionExcludesProjectsOutsideTheSolution()
    {
        using var repo = new TemporaryGitRepository(); repo.Write("src/A.csproj", Project); repo.Write("other/B.csproj", Project); repo.Write("App.slnx", "<Solution><Project Path=\"src/A.csproj\" /></Solution>");
        var node = JsonSerializer.SerializeToNode(await DotnetFacts.Inspect(repo.Root, "App.slnx"), AgentTool.Json)!;
        Assert.Single(node["projects"]!.AsArray()); Assert.Equal("src/A.csproj", node["projects"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlansAreStructuredAndDoNotExecuteBuilds()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/A.csproj", Project); repo.Write("tests/A.Tests.csproj", Project.Replace("</PropertyGroup>", "<IsTestProject>true</IsTestProject></PropertyGroup>").Replace("</Project>", "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.1\" /><ProjectReference Include=\"../src/A.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));
        var build = JsonSerializer.SerializeToNode(await DotnetFacts.BuildPlan(repo.Root, "src/A.csproj", null, "Release", true), AgentTool.Json)!;
        Assert.Equal("restore", build["commands"]![0]!["arguments"]![0]!.GetValue<string>());
        Assert.Contains("-bl:.agent-tool/binlogs/", build["commands"]![1]!["arguments"]!.AsArray().Last()!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("structured-binlog-query", build["artifacts"]!["analysisOrder"]![0]!.GetValue<string>());
        Assert.Equal("bounded-text-log-fallback", build["artifacts"]!["analysisOrder"]![1]!.GetValue<string>());
        var test = JsonSerializer.SerializeToNode(await DotnetFacts.TestPlan(repo.Root, "src/A.csproj", null, "Release", new(null, null, null, "Category=Fast")), AgentTool.Json)!;
        Assert.Equal("vstest", test["tests"]![0]!["platform"]!.GetValue<string>());
        Assert.Contains("Category=Fast", test["tests"]![0]!["command"]!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool")));
    }

    [Fact]
    public async Task TestPlanUsesNativeMtpShapeAndFrameworkSpecificSelection()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestPatch\"},\"test\":{\"runner\":\"Microsoft.Testing.Platform\"}}");
        repo.Write("tests/X.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"3.2.1\" /></ItemGroup></Project>");
        var node = JsonSerializer.SerializeToNode(await DotnetFacts.TestPlan(repo.Root, "tests/X.Tests.csproj", null, "Debug", new("Ns.Type.Method", null, null, null)), AgentTool.Json)!;
        var row = node["tests"]![0]!; var arguments = row["command"]!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal("microsoft-testing-platform", row["platform"]!.GetValue<string>());
        Assert.Equal("xunit-v3", row["framework"]!.GetValue<string>());
        Assert.Equal("mtp-native", row["commandMode"]!.GetValue<string>());
        Assert.Equal(new[] { "test", "--project", "tests/X.Tests.csproj" }, arguments.Take(3));
        Assert.Contains("--filter-method", arguments); Assert.DoesNotContain("--", arguments);
    }

    [Fact]
    public async Task TestPlanPlacesMtpBridgeArgumentsAfterSeparator()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("tests/M.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><IsTestingPlatformApplication>true</IsTestingPlatformApplication><TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"MSTest.TestFramework\" Version=\"4.0.1\" /></ItemGroup></Project>");
        var node = JsonSerializer.SerializeToNode(await DotnetFacts.TestPlan(repo.Root, "tests/M.Tests.csproj", null, "Debug", new(null, "Ns.Type", null, null)), AgentTool.Json)!;
        var row = node["tests"]![0]!; var arguments = row["command"]!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal("mtp-bridge", row["commandMode"]!.GetValue<string>());
        Assert.True(Array.IndexOf(arguments, "--") < Array.IndexOf(arguments, "--filter"));
        Assert.Contains("FullyQualifiedName~Ns.Type", arguments);
    }

    [Fact]
    public async Task TestPlanRejectsAmbiguousRawMtpFilters()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestPatch\"},\"test\":{\"runner\":\"Microsoft.Testing.Platform\"}}");
        repo.Write("tests/T.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup><ItemGroup><PackageReference Include=\"TUnit\" Version=\"1.0.0\" /></ItemGroup></Project>");
        await Assert.ThrowsAsync<InvalidOperationException>(() => DotnetFacts.TestPlan(repo.Root, "tests/T.Tests.csproj", null, "Debug", new(null, null, null, "Name~Fast")));
    }

    [Fact]
    public void ParsesTrxAndJUnitResults()
    {
        var root = AgentTool.FindToolkit();
        var trx = JsonSerializer.SerializeToNode(DotnetArtifacts.TestResults(Path.Combine(root, "tests/fixtures/test-results/sample.trx"), new()), AgentTool.Json)!;
        var junit = JsonSerializer.SerializeToNode(DotnetArtifacts.TestResults(Path.Combine(root, "tests/fixtures/test-results/sample.junit.xml"), new()), AgentTool.Json)!;
        Assert.Equal(3, trx["total"]!.GetValue<int>()); Assert.Equal(500, trx["durationMilliseconds"]!.GetValue<double>());
        Assert.Equal(1, junit["outcomes"]!["Failed"]!.GetValue<int>()); Assert.Equal(750, junit["durationMilliseconds"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("cobertura.xml", "cobertura")]
    [InlineData("opencover.xml", "opencover")]
    public void ParsesCoverageFormats(string file, string format)
    {
        var path = Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/coverage", file);
        var node = JsonSerializer.SerializeToNode(DotnetArtifacts.Coverage(path, new()), AgentTool.Json)!;
        Assert.Equal(format, node["format"]!.GetValue<string>()); Assert.Equal(75, node["branches"]!["percent"]!.GetValue<double>());
    }

    [Fact]
    public async Task DiagnosticsPlanSelectsOneBoundedSignalAndReportsEnvironment()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DotnetFacts.DiagnosticsPlan("0"));
        await Assert.ThrowsAsync<ArgumentException>(() => DotnetFacts.DiagnosticsPlan("42", "unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => DotnetFacts.DiagnosticsPlan("42", "cpu", "301"));
        var node = JsonSerializer.SerializeToNode(await DotnetFacts.DiagnosticsPlan("42", "contention", "20"), AgentTool.Json)!;
        Assert.Equal(5, node["tools"]!.AsArray().Count);
        Assert.Equal("contention", node["signal"]!.GetValue<string>());
        Assert.Equal(20, node["durationSeconds"]!.GetValue<int>());
        Assert.NotNull(node["environment"]!["os"]);
        var plans = node["collection"]!["plans"]!.AsArray(); Assert.Single(plans);
        Assert.Equal("dotnet-trace", plans[0]!["executable"]!.GetValue<string>());
        Assert.Contains("00:00:20", plans[0]!["arguments"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public void DependencyInventorySeparatesDirectAndTransitivePackages()
    {
        var root = AgentTool.FindToolkit(); var path = Path.Combine(root, "tests/fixtures/nuget/dependency-report.json");
        var node = JsonSerializer.SerializeToNode(DotnetArtifacts.Dependencies(path, root, new()), AgentTool.Json)!;
        Assert.Equal(1, node["direct"]!.GetValue<int>()); Assert.Equal(1, node["transitive"]!.GetValue<int>());
        Assert.Equal("1.0.1", node["packages"]![0]!["resolvedVersion"]!.GetValue<string>());
    }
}
