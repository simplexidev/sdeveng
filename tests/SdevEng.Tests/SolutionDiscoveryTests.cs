namespace SdevEng.Tests;

public class SolutionDiscoveryTests
{
    [Fact]
    public async Task MapsDirectTestReferencesAndProjectDependencyCandidates()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("A/Thing.cs", "namespace Demo; public class Thing { public void Run() {} public void Other() {} }");
        repo.Write("Tests/Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        repo.Write("Tests/Tests.cs", "namespace Xunit { public class FactAttribute : System.Attribute {} } namespace Demo { public class Tests { [Xunit.Fact] public void CallsRun() { new Thing().Run(); } [Xunit.Fact] public void Unrelated() {} } }");
        Assert.Equal(0, (await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root)).ExitCode);
        Assert.Equal(0, (await Processes.Run("dotnet", ["sln", "App.sln", "add", "A/A.csproj", "Tests/Tests.csproj"], repo.Root)).ExitCode);

        var model = await Projects.SemanticModel(repo.Root, "App.sln");
        var result = model.TestCandidates("M:Demo.Thing.Run", 1);
        Assert.Equal(2, result.Total);
        Assert.True(result.Truncated);
        Assert.Contains(model.TestCandidates("M:Demo.Thing.Run", 2).Candidates, item => item.TestKey == "M:Demo.Tests.CallsRun" && item.Reason == "direct-reference");
        Assert.Contains(model.TestCandidates("M:Demo.Thing.Other", 2).Candidates, item => item.TestKey is null && item.Reason == "project-dependency");
        Assert.DoesNotContain(model.TestCandidates("M:Demo.Thing.Other", 2).Candidates, item => item.TestKey == "M:Demo.Tests.Unrelated");
        Assert.Throws<ArgumentException>(() => model.TestCandidates("M:Missing", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.TestCandidates("M:Demo.Thing.Run", 0));
        var symbols = model.AffectedSymbols("M:Demo.Thing.Run", 1);
        Assert.Equal(2, symbols.Total);
        Assert.True(symbols.Truncated);
        Assert.Contains("M:Demo.Tests.CallsRun", model.AffectedSymbols("M:Demo.Thing.Run", 10).Items);
        Assert.Equal(new[] { "A/Thing.cs", "Tests/Tests.cs" }, model.AffectedFiles("A/Thing.cs", 10).Items);
        Assert.Throws<ArgumentException>(() => model.AffectedFiles("missing.cs", 10));
        Assert.Throws<ArgumentException>(() => model.AffectedSymbols("M:Missing", 10));
        var routed = await new AgentTool.DotnetCommandModule().Execute(Cli.Parse(["dotnet", "test-candidates", "--project", "App.sln", "--symbol", "M:Demo.Thing.Run"]),
            AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("ok", routed.Status);
        Assert.Contains("direct-reference", System.Text.Json.JsonSerializer.Serialize(routed.Data, AgentTool.Json), StringComparison.Ordinal);
        var filesCommand = await new AgentTool.DotnetCommandModule().Execute(Cli.Parse(["dotnet", "affected-files", "--project", "App.sln", "--path", "A/Thing.cs"]),
            AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("ok", filesCommand.Status);
        Assert.Contains("Tests/Tests.cs", System.Text.Json.JsonSerializer.Serialize(filesCommand.Data, AgentTool.Json), StringComparison.Ordinal);
        var symbolsCommand = await new AgentTool.DotnetCommandModule().Execute(Cli.Parse(["dotnet", "affected-symbols", "--project", "App.sln", "--symbol", "M:Demo.Thing.Run"]),
            AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("ok", symbolsCommand.Status);
        Assert.Contains("M:Demo.Tests.CallsRun", System.Text.Json.JsonSerializer.Serialize(symbolsCommand.Data, AgentTool.Json), StringComparison.Ordinal);
    }
    [Fact]
    public async Task IndexesOnlyAttributedMethodsInTestProjects()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("Tests/Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        repo.Write("Tests/Tests.cs", "namespace Xunit { public class FactAttribute : System.Attribute {} public class TheoryAttribute : FactAttribute {} } namespace Demo { public class Tests { [Xunit.Fact] public void A() {} [Xunit.Theory] public void B() {} public void Helper() {} } }");
        Assert.Equal(0, (await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root)).ExitCode);
        Assert.Equal(0, (await Processes.Run("dotnet", ["sln", "App.sln", "add", "Tests/Tests.csproj"], repo.Root)).ExitCode);
        var project = Assert.Single((await Projects.SemanticModel(repo.Root, "App.sln")).Projects);
        Assert.True(project.IsTest);
        Assert.Equal("xunit-v2", project.TestFramework);
        Assert.Equal(new[] { "M:Demo.Tests.A", "M:Demo.Tests.B" }, project.TestMethods.Select(item => item.StableKey));
        Assert.All(project.TestMethods, item => Assert.Contains("Tests/Tests.cs:", item.Location, StringComparison.Ordinal));
    }
    [Fact] public void FindsBothSolutionFormats() { using var repo = new TemporaryGitRepository(); repo.Write("A.sln", "Microsoft Visual Studio Solution File, Format Version 12.00"); repo.Write("nested/B.slnx", "<Solution />"); repo.Write("obj/ignored.slnx", "<Solution />"); Assert.Equal(2, Projects.Solutions(repo.Root).Length); }

    [Fact]
    public async Task LoadsSolutionProjectsThroughMsbuildWorkspace()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var project = Path.Combine(repo.Root, "src", "App.csproj");
        var create = await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root);
        Assert.True(create.ExitCode == 0, create.Output);
        var add = await Processes.Run("dotnet", ["sln", "App.sln", "add", project], repo.Root);
        Assert.True(add.ExitCode == 0, add.Output);

        var result = await Projects.LoadSolution(repo.Root, "App.sln");

        Assert.Equal(Path.Combine(repo.Root, "App.sln"), result.Path);
        Assert.True(result.Projects.SequenceEqual([project]), System.Text.Json.JsonSerializer.Serialize(result.Diagnostics));
        Assert.Equal([project], result.CompilationAvailableProjects);
        Assert.Equal(result.Diagnostics.OrderBy(item => item.ProjectPath, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal), result.Diagnostics);
    }

    [Fact]
    public async Task CreatesDeterministicSemanticModelForCSharpSolution()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("src/Thing.cs", "namespace Demo; public class Thing : Base, IThing { public Thing() { } public string Name => \"thing\"; public int Count; public event System.Action? Changed; public void Run() { } } public interface IBase { } public interface IThing : IBase { } public class Base { }");
        var project = Path.Combine(repo.Root, "src", "App.csproj");
        var create = await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root);
        Assert.Equal(0, create.ExitCode);
        var add = await Processes.Run("dotnet", ["sln", "App.sln", "add", project], repo.Root);
        Assert.Equal(0, add.ExitCode);

        var result = await Projects.SemanticModel(repo.Root, "App.sln");
        var repeated = await Projects.SemanticModel(repo.Root, "App.sln");

        var projectModel = Assert.Single(result.Projects);
        Assert.Equal("Demo", Assert.Single(projectModel.Namespaces).Name);
        Assert.Equal(new[] { "global::Demo.Base", "global::Demo.IBase", "global::Demo.IThing", "global::Demo.Thing" }, projectModel.Types.Select(item => item.Name));
        var contract = Assert.Single(projectModel.Types, item => item.Name == "global::Demo.IThing");
        Assert.Equal("Interface", contract.Kind);
        Assert.Equal(new[] { "global::Demo.IBase" }, contract.BaseTypes);
        var type = Assert.Single(projectModel.Types, item => item.Name == "global::Demo.Thing");
        Assert.Equal("global::Demo.Thing", type.Name);
        Assert.Equal("Class", type.Kind);
        Assert.Equal("T:Demo.Thing", type.StableKey);
        Assert.EndsWith("src/Thing.cs:1:30", type.Location, StringComparison.Ordinal);
        Assert.Equal(type.StableKey, Assert.Single(repeated.Projects).Types.Single(item => item.Name == type.Name).StableKey);
        Assert.Equal(new[] { "global::Demo.Base", "global::Demo.IThing" }, type.BaseTypes);
        Assert.Equal(new[]
        {
            new SemanticTypeRelationshipModel("T:Demo.IThing", "T:Demo.IBase", "inherits"),
            new SemanticTypeRelationshipModel("T:Demo.Thing", "T:Demo.IThing", "implements"),
            new SemanticTypeRelationshipModel("T:Demo.Thing", "T:Demo.Base", "inherits")
        }, projectModel.TypeRelationships);
        Assert.Equal(projectModel.TypeRelationships, Assert.Single(repeated.Projects).TypeRelationships);
        Assert.Contains(type.Members, member => member.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(type.Callables, callable => callable.Kind == "Constructor" && callable.Location.EndsWith(":1:60", StringComparison.Ordinal));
        Assert.Contains(type.Callables, callable => callable.Kind == "Method" && callable.Name == "Run" && callable.Location.EndsWith(":1:170", StringComparison.Ordinal));
        Assert.Contains(type.Callables, callable => callable.Name.Contains("Run", StringComparison.Ordinal) && callable.StableKey.StartsWith("M:Demo.Thing.Run", StringComparison.Ordinal));
        Assert.Contains(type.DataMembers, member => member.Kind == "Property" && member.Name.Contains("Name", StringComparison.Ordinal) && member.Location.EndsWith(":1:86", StringComparison.Ordinal));
        Assert.Contains(type.DataMembers, member => member.Kind == "Field" && member.Name.Contains("Count", StringComparison.Ordinal) && member.Location.EndsWith(":1:114", StringComparison.Ordinal));
        Assert.Contains(type.DataMembers, member => member.Kind == "Event" && member.Name.Contains("Changed", StringComparison.Ordinal) && member.Location.EndsWith(":1:149", StringComparison.Ordinal));
        Assert.All(type.DataMembers, member => Assert.False(string.IsNullOrWhiteSpace(member.StableKey)));
    }

    [Fact]
    public async Task IndexesReferencesAndCallSitesWithStableTargets()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("src/Thing.cs", "namespace Demo; public class Thing { public void Run() { Helper(); } public void Helper() { } } public class Use { public void Go() { var thing = new Thing(); thing.Run(); } }");
        Assert.Equal(0, (await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root)).ExitCode);
        Assert.Equal(0, (await Processes.Run("dotnet", ["sln", "App.sln", "add", "src/App.csproj"], repo.Root)).ExitCode);

        var project = Assert.Single((await Projects.SemanticModel(repo.Root, "App.sln")).Projects);
        Assert.Contains(project.References, item => item.TargetKey == "T:Demo.Thing" && item.CallerKey == "M:Demo.Use.Go");
        Assert.Contains(project.CallSites, item => item.TargetKey == "M:Demo.Thing.Run" && item.CallerKey == "M:Demo.Use.Go" && item.Location.Contains("src/Thing.cs:", StringComparison.Ordinal));
        Assert.Contains(project.CallSites, item => item.TargetKey == "M:Demo.Thing.Helper" && item.CallerKey == "M:Demo.Thing.Run");
        Assert.Contains(project.CallSites, item => item.TargetKey == "M:Demo.Thing.#ctor" && item.CallerKey == "M:Demo.Use.Go");
        var repeated = Assert.Single((await Projects.SemanticModel(repo.Root, "App.sln")).Projects);
        Assert.Equal(project.References, repeated.References);
        Assert.Equal(project.CallSites, repeated.CallSites);
    }

    [Fact]
    public async Task IndexesCrossProjectEdgesAndBoundsRelationshipQuery()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("A/Thing.cs", "namespace Demo; public class Thing { public void Run() { } }");
        repo.Write("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>");
        repo.Write("B/Use.cs", "namespace Demo; public class Use { public void Go() { var thing = new Thing(); thing.Run(); } }");
        Assert.Equal(0, (await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root)).ExitCode);
        Assert.Equal(0, (await Processes.Run("dotnet", ["sln", "App.sln", "add", "A/A.csproj", "B/B.csproj"], repo.Root)).ExitCode);

        var model = await Projects.SemanticModel(repo.Root, "App.sln");
        Assert.Contains(model.ProjectEdges, edge => edge.SourceProject == "B/B.csproj" && edge.TargetProject == "A/A.csproj" && edge.TargetKey == "M:Demo.Thing.Run" && edge.Kind == "call");
        Assert.Equal(model.ProjectEdges, (await Projects.SemanticModel(repo.Root, "App.sln")).ProjectEdges);
        var query = model.Relationships("A/A.csproj", 1);
        Assert.Single(query.Edges);
        Assert.True(query.Truncated);
        Assert.Equal(model.ProjectEdges.Length, query.Total);
        Assert.Throws<ArgumentException>(() => model.Relationships("missing.csproj", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Relationships("A/A.csproj", 0));
        var routed = await new AgentTool.DotnetCommandModule().Execute(
            Cli.Parse(["dotnet", "relationships", "--project", "App.sln", "--path", "A/A.csproj"]),
            AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal("ok", routed.Status);
        Assert.Contains("M:Demo.Thing.Run", System.Text.Json.JsonSerializer.Serialize(routed.Data, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsSolutionWithIncompleteSemanticState()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("src/Thing.cs", "public class Thing { public Missing Name => null; }");
        var create = await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root);
        Assert.Equal(0, create.ExitCode);
        var add = await Processes.Run("dotnet", ["sln", "App.sln", "add", "src/App.csproj"], repo.Root);
        Assert.Equal(0, add.ExitCode);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Projects.SemanticModel(repo.Root, "App.sln"));
        Assert.Contains("incomplete semantic state", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
