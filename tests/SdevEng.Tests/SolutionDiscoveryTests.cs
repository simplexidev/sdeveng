namespace SdevEng.Tests;

public class SolutionDiscoveryTests
{
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
        repo.Write("src/Thing.cs", "namespace Demo; public class Thing { public string Name => \"thing\"; }");
        var project = Path.Combine(repo.Root, "src", "App.csproj");
        var create = await Processes.Run("dotnet", ["new", "sln", "-n", "App", "--format", "sln", "--force"], repo.Root);
        Assert.Equal(0, create.ExitCode);
        var add = await Processes.Run("dotnet", ["sln", "App.sln", "add", project], repo.Root);
        Assert.Equal(0, add.ExitCode);

        var result = await Projects.SemanticModel(repo.Root, "App.sln");

        var type = Assert.Single(Assert.Single(result.Projects).Types);
        Assert.Equal("global::Demo.Thing", type.Name);
        Assert.Equal("Class", type.Kind);
        Assert.Contains(type.Members, member => member.Contains("Name", StringComparison.Ordinal));
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
