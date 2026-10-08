using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(new[] { plain, host, "unknown", "absent" }, facts.Select(fact => fact.Status));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void ReportsStaticRedirectionUseAndResolvedCommandLineVersion()
    {
        var facts = FrameworkCapabilities.Detect("Exe",
            Compile("if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected) System.Console.WriteLine(1);"),
            new Dictionary<string, string?> { ["System.CommandLine"] = "2.0.0" });
        Assert.Equal("detected", facts.Single(fact => fact.Id == "console-redirection").Status);
        var commandLine = facts.Single(fact => fact.Id == "system-commandline");
        Assert.Equal("detected", commandLine.Status);
        Assert.Equal("2.0.0", commandLine.Version);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"))
            .Single(fact => fact.Id == "console-redirection").Status);
    }

    [Fact]
    public void LibrariesAndUnavailableCompilationsDoNotClaimConsoleSupport()
    {
        Assert.Equal(new[] { "absent", "absent", "absent", "absent" }, FrameworkCapabilities.Detect("Library", Compile("System.Console.WriteLine(1);")).Select(fact => fact.Status));
        Assert.Equal(new[] { "unknown", "unknown", "unknown", "absent" }, FrameworkCapabilities.Detect("Exe", null).Select(fact => fact.Status));
    }

    [Fact]
    public void RegisteredConsoleFixturesMatchIndependentExpectationsAndSchemas()
    {
        var root = AgentTool.FindToolkit();
        var fixturePath = Path.Combine(root, "tests/SdevEng.Tests/Fixtures/ConsoleDetection/cases.json");
        var fixtures = JsonNode.Parse(File.ReadAllText(fixturePath))!;
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/console-detection-fixtures.schema.json"))
            .Evaluate(fixtures).IsValid);
        var capabilitySchema = JsonSchema.FromFile(Path.Combine(root, "schemas/framework-capabilities.schema.json"));
        var cases = fixtures["cases"]!.AsArray();
        Assert.Equal(new[] { "plain-console", "hosted-console", "redirected-io", "unsupported-commandline-version" },
            cases.Select(item => item!["id"]!.GetValue<string>()));
        foreach (var item in cases)
        {
            var packages = item!["packageVersions"]!.AsObject().ToDictionary(pair => pair.Key,
                pair => pair.Value?.GetValue<string>());
            var facts = FrameworkCapabilities.Detect(item["outputType"]!.GetValue<string>(),
                Compile(item["source"]!.GetValue<string>()), packages);
            Assert.Equal(item["expected"]!.AsArray().Select(value => value!.GetValue<string>()),
                facts.Select(fact => fact.Status));
            Assert.True(capabilitySchema.Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
            if (item["expectedCommandLineSkillActivation"] is JsonValue expectedActivation)
            {
                var version = facts.Single(fact => fact.Id == "system-commandline").Version;
                Assert.Equal(expectedActivation.GetValue<bool>(), version == "2.0.0");
                Assert.Equal("1.0.0", version);
            }
        }
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
        repo.Write("CommandLine/CommandLine.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"System.CommandLine\" Version=\"2.0.0\" /></ItemGroup></Project>");
        repo.Write("CommandLine/Program.cs", "System.Console.WriteLine(1);");
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
                ["App.csproj"] = ["detected", "absent", "unknown", "absent"],
                ["Hosted/Hosted.csproj"] = ["absent", "detected", "unknown", "absent"],
                ["Unused/Unused.csproj"] = ["detected", "absent", "unknown", "absent"],
                ["CommandLine/CommandLine.csproj"] = ["detected", "absent", "unknown", "detected"],
                ["Broken/Broken.csproj"] = ["unknown", "unknown", "unknown", "absent"],
                ["Library/Library.csproj"] = ["absent", "absent", "absent", "absent"],
                ["Unsupported/Unsupported.vbproj"] = ["unknown", "unknown", "unknown", "absent"]
            };
            Assert.Equal(expected.Count, json["projects"]!.AsArray().Count);
            foreach (var row in json["projects"]!.AsArray())
            {
                var facts = row!["frameworkCapabilities"]!;
                Assert.Equal(expected[row["path"]!.GetValue<string>()], facts.AsArray().Select(fact => fact!["status"]!.GetValue<string>()));
                Assert.Equal(new[] { "plain-console", "generic-host-console", "console-redirection", "system-commandline" }, facts.AsArray().Select(fact => fact!["id"]!.GetValue<string>()));
                if (row["path"]!.GetValue<string>() == "CommandLine/CommandLine.csproj")
                    Assert.Equal("2.0.0", facts[3]!["version"]!.GetValue<string>());
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
            }
            if (command[0] == "repo")
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json")).Evaluate(json).IsValid);
        }
    }
}
