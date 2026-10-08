using System.Text.Json;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Hosting;

namespace SdevEng.Tests;

public sealed class FrameworkCapabilitiesTests
{
    static CSharpCompilation Compile(string source) => CSharpCompilation.Create("Fixture",
        [CSharpSyntaxTree.ParseText(source)],
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Host).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.ConsoleApplication));

    [Theory]
    [InlineData("System.Console.WriteLine(1);", "detected", "absent")]
    [InlineData("class Program { static void Main() { System.Console.WriteLine(1); } }", "detected", "absent")]
    [InlineData("var host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();", "absent", "detected")]
    [InlineData("var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();", "absent", "detected")]
    [InlineData("Missing.Run();", "unknown", "unknown")]
    [InlineData("class Program { static void Main() { Configure(); } static void Configure() {} }", "unknown", "unknown")]
    [InlineData("class Program { static void Main() {} static void Unused() { Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(); } }", "detected", "absent")]
    [InlineData("System.Action unused = () => Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();", "detected", "absent")]
    [InlineData("void Unused() { Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(); }", "detected", "absent")]
    public void UsesEntryPointSymbolsRatherThanInstalledPackagesOrUncalledCode(string source, string plain, string host)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        Assert.Equal(new[] { plain, host }, facts.Select(fact => fact.Status));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void LibrariesAndUnavailableCompilationsDoNotClaimConsoleSupport()
    {
        Assert.All(FrameworkCapabilities.Detect("Library", Compile("System.Console.WriteLine(1);")), fact => Assert.Equal("absent", fact.Status));
        Assert.All(FrameworkCapabilities.Detect("Exe", null), fact => Assert.Equal("unknown", fact.Status));
    }

    [Fact]
    public async Task NormalDiscoveryProjectsTheSharedFactsAndValidatesTheContract()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("Program.cs", "System.Console.WriteLine(1);");
        // Independent project fixtures, including installed-but-unused Hosting.
        repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>");
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.12\" /></ItemGroup></Project>";
        repo.Write("Hosted/Hosted.csproj", project);
        repo.Write("Hosted/Program.cs", "var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();");
        repo.Write("Unused/Unused.csproj", project);
        repo.Write("Unused/Program.cs", "System.Console.WriteLine(1);");
        repo.Write("Broken/Broken.csproj", project);
        repo.Write("Broken/Program.cs", "Missing.Run();");
        repo.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        repo.Write("Unsupported/Unsupported.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");
        repo.Commit();
        foreach (var path in Projects.Discover(repo.Root))
        {
            var restore = await Processes.Run("dotnet", ["restore", path, "--nologo"], repo.Root);
            Assert.Equal(0, restore.ExitCode);
        }
        foreach (var command in new[] { new[] { "repo", "describe" }, new[] { "dotnet", "inspect" } })
        {
            var result = await CommandTestRuntime.Execute(Cli.Parse(command), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
            var json = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            var expected = new Dictionary<string, string[]>
            {
                ["App.csproj"] = ["detected", "absent"],
                ["Hosted/Hosted.csproj"] = ["absent", "detected"],
                ["Unused/Unused.csproj"] = ["detected", "absent"],
                ["Broken/Broken.csproj"] = ["unknown", "unknown"],
                ["Library/Library.csproj"] = ["absent", "absent"],
                ["Unsupported/Unsupported.vbproj"] = ["unknown", "unknown"]
            };
            Assert.Equal(expected.Count, json["projects"]!.AsArray().Count);
            foreach (var row in json["projects"]!.AsArray())
            {
                var facts = row!["frameworkCapabilities"]!;
                Assert.Equal(expected[row["path"]!.GetValue<string>()], facts.AsArray().Select(fact => fact!["status"]!.GetValue<string>()));
                Assert.Equal(new[] { "plain-console", "generic-host-console" }, facts.AsArray().Select(fact => fact!["id"]!.GetValue<string>()));
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
            }
            if (command[0] == "repo")
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json")).Evaluate(json).IsValid);
        }
    }
}
