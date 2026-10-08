using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SdevEng.Tests;

// Synthetic API fixture, shaped from the pinned 11.3.0 source; never runtime qualification.
public sealed class AvaloniaDetectionTests
{
    static readonly MetadataReference[] Platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    const string Api = """
        [assembly: System.Reflection.AssemblyVersion("11.0.0.0")]
        namespace Avalonia {
            public class Application {}
            public class AppBuilder {}
            public static class ClassicDesktopStyleApplicationLifetimeExtensions {
                public static int StartWithClassicDesktopLifetime(this AppBuilder builder, string[] args) => 0;
                public static AppBuilder SetupWithClassicDesktopLifetime(this AppBuilder builder, string[] args) => builder;
            }
        }
        namespace Avalonia.Controls { public class Control {} }
        namespace Avalonia.Controls.ApplicationLifetimes {
            public interface IClassicDesktopStyleApplicationLifetime {}
            public interface ISingleViewApplicationLifetime {}
        }
        """;
    static byte[] Assembly(string name, string source, IEnumerable<MetadataReference>? references = null)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references ?? Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        return stream.ToArray();
    }
    static JsonNode Registry() => JsonNode.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "upstream/dotnet-skills.json")))!;

    [Fact]
    public void RegisteredAvaloniaFixturesMatchIndependentExpectationsAndSchemas()
    {
        var root = AgentTool.FindToolkit();
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/SdevEng.Tests/Fixtures/AvaloniaDetection/cases.json")))!;
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/avalonia-detection-fixtures.schema.json"))
            .Evaluate(fixtures).IsValid);
        var capabilitySchema = JsonSchema.FromFile(Path.Combine(root, "schemas/framework-capabilities.schema.json"));
        var cases = fixtures["cases"]!.AsArray();
        Assert.Equal(new[] { "code-only", "xaml-and-theme", "mixed", "unrelated-project", "unsupported-major" },
            cases.Select(item => item!["id"]!.GetValue<string>()));
        foreach (var item in cases)
        {
            var expected = item!["expected"]!;
            var source = item["source"]!.GetValue<string>();
            var compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(source)],
                Platform.Append(MetadataReference.CreateFromImage(Assembly("Avalonia.Controls", Api))),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var properties = new Dictionary<string, string?>();
            if (item["compiledBindings"] is JsonValue setting) properties["AvaloniaUseCompiledBindingsByDefault"] = setting.GetValue<string>();
            var facts = FrameworkCapabilities.Detect("Library", compilation,
                new Dictionary<string, string?> { ["Avalonia"] = item["packageVersion"]!.GetValue<string>() },
                properties, item["files"]!.AsArray().Select(file => file!.GetValue<string>()).ToArray(), Registry());
            Assert.Equal(expected["package"]!.GetValue<string>(), facts.Single(fact => fact.Id == "avalonia").Status);
            Assert.Equal(expected["structure"]!.GetValue<string>(), facts.Single(fact => fact.Id == "avalonia-structure").Evidence);
            Assert.Equal(expected["themes"]!.GetValue<string>(), facts.Single(fact => fact.Id == "avalonia-themes").Status);
            Assert.Equal(expected["compiledBindings"]!.GetValue<string>(), facts.Single(fact => fact.Id == "avalonia-compiled-bindings").Status);
            Assert.True(capabilitySchema.Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
        }
    }

    [Theory]
    [InlineData("class App : Avalonia.Application { Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime; }", "", "static-lifetime-types:IClassicDesktopStyleApplicationLifetime", "code-only")]
    [InlineData("class App : Avalonia.Application { Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime lifetime; }", "App.axaml", "static-lifetime-types:ISingleViewApplicationLifetime", "xaml")]
    [InlineData("class App : Avalonia.Application {} class View : Avalonia.Controls.Control {}", "App.axaml", "no-resolved-lifetime-types", "mixed")]
    [InlineData("class Unrelated {}", "", "no-resolved-lifetime-types", "no-avalonia-ui-structure")]
    [InlineData("namespace Pretend { class Control {} class View : Control {} interface ISingleViewApplicationLifetime {} class App { ISingleViewApplicationLifetime lifetime; } }", "", "no-resolved-lifetime-types", "no-avalonia-ui-structure")]
    [InlineData("using Avalonia; class App : Application { void Start() { new AppBuilder().StartWithClassicDesktopLifetime([]); } }", "", "static-lifetime-types:IClassicDesktopStyleApplicationLifetime", "code-only")]
    public void StaticFactsDistinguishLifetimeAndStructure(string source, string file, string lifetime, string structure)
    {
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(source)],
            Platform.Append(MetadataReference.CreateFromImage(Assembly("Avalonia.Controls", Api))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var facts = FrameworkCapabilities.Detect("Library", compilation,
            new Dictionary<string, string?> { ["Avalonia"] = "11.3.0" }, avaloniaFiles: file.Length == 0 ? [] : [file], provenance: Registry());
        Assert.Equal(lifetime, facts.Single(item => item.Id == "avalonia-lifetime").Evidence);
        Assert.Equal(structure, facts.Single(item => item.Id == "avalonia-structure").Evidence);
        Assert.Equal(structure == "no-avalonia-ui-structure" ? "absent" : "detected", facts.Single(item => item.Id == "avalonia-structure").Status);
        Assert.Equal(file.Length == 0 ? "absent" : "detected", facts.Single(item => item.Id == "avalonia-resources").Status);
        Assert.Equal("unknown", facts.Single(item => item.Id == "avalonia-compiled-bindings").Status);
        Validate(facts);
    }

    [Theory]
    [InlineData("12.0.0", "unsupported-package-version")]
    [InlineData("unresolved", "unsupported-package-version")]
    [InlineData("11.3.0", "unavailable-or-incomplete-csharp-compilation")]
    public void UnsupportedAndUnresolvedFactsStayUnknown(string version, string evidence)
    {
        var facts = FrameworkCapabilities.Detect(null, null, new Dictionary<string, string?> { ["Avalonia"] = version },
            avaloniaFiles: ["App.axaml"], provenance: Registry());
        foreach (var id in new[] { "avalonia-lifetime", "avalonia-structure" })
        {
            Assert.Equal("unknown", facts.Single(item => item.Id == id).Status);
            Assert.Equal(evidence, facts.Single(item => item.Id == id).Evidence);
        }
        Validate(facts);
    }

    [Fact]
    public void MissingEvaluatedItemsAndMissingPinsDoNotInventStructure()
    {
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText("class App : Avalonia.Application {}")],
            Platform.Append(MetadataReference.CreateFromImage(Assembly("Avalonia.Controls", Api))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var packages = new Dictionary<string, string?> { ["Avalonia"] = "11.3.0" };
        var facts = FrameworkCapabilities.Detect("Library", compilation, packages, provenance: Registry());
        Assert.Equal("unknown", facts.Single(item => item.Id == "avalonia-structure").Status);
        Assert.Equal("evaluated-xaml-items-unavailable", facts.Single(item => item.Id == "avalonia-structure").Evidence);
        var registry = Registry();
        registry["frameworks"]!.AsArray().Single(item => item!["frameworkId"]!.GetValue<string>() == "avalonia")!["sourceRevision"] = "unknown";
        facts = FrameworkCapabilities.Detect("Library", compilation, packages, avaloniaFiles: [], provenance: registry);
        Assert.All(facts.Where(item => item.Id.StartsWith("avalonia", StringComparison.Ordinal)), item => Assert.Equal("unknown", item.Status));
        Validate(facts);
    }

    [Fact]
    public async Task DiscoveryCommandsUseRegistryAndEvaluatedXamlItems()
    {
        using var repo = new TemporaryGitRepository();
        repo.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault></PropertyGroup>
            <ItemGroup><Reference Include="Avalonia"><HintPath>Avalonia.dll</HintPath></Reference>
            <Reference Include="Avalonia.Controls"><HintPath>Avalonia.Controls.dll</HintPath></Reference>
            <AvaloniaResource Include="App.axaml" />
            <AvaloniaResource Include="Themes/DefaultTheme.axaml" />
            <Compile Remove="Negative/**/*.cs;Unresolved/**/*.cs;Unsupported/**/*.cs" /></ItemGroup></Project>
            """);
        var targetingPack = await Processes.Run("dotnet", ["msbuild", "App.csproj", "-nologo", "-getProperty:NetCoreTargetingPackRoot"], repo.Root);
        Assert.Equal(0, targetingPack.ExitCode);
        var packRoot = Path.Combine(targetingPack.Output.Trim(), "Microsoft.NETCore.App.Ref");
        var installedPack = Directory.GetDirectories(packRoot)
            .Where(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Major == 10)
            .OrderByDescending(path => Version.Parse(Path.GetFileName(path))).First();
        var referenceDirectory = Path.Combine(installedPack, "ref", "net10.0");
        var references = Directory.GetFiles(referenceDirectory, "*.dll").Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        File.WriteAllBytes(Path.Combine(repo.Root, "Avalonia.Controls.dll"), Assembly("Avalonia.Controls", Api, references));
        File.WriteAllBytes(Path.Combine(repo.Root, "Avalonia.dll"), Assembly("Avalonia", "[assembly: System.Reflection.AssemblyVersion(\"11.3.0.0\")] public class Marker {}", references));
        repo.Write("App.cs", "class App : Avalonia.Application { Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime lifetime; }");
        repo.Write("App.axaml", "<Application xmlns=\"https://github.com/avaloniaui\" />");
        repo.Write("Themes/DefaultTheme.axaml", "<Styles xmlns=\"https://github.com/avaloniaui\" />");
        var projectXml = File.ReadAllText(Path.Combine(repo.Root, "App.csproj"))
            .Replace("<HintPath>", "<HintPath>../", StringComparison.Ordinal)
            .Replace("<AvaloniaResource Include=\"App.axaml\" />", "", StringComparison.Ordinal)
            .Replace("<AvaloniaResource Include=\"Themes/DefaultTheme.axaml\" />", "", StringComparison.Ordinal);
        repo.Write("Negative/Negative.csproj", projectXml);
        repo.Write("Negative/Source.cs", "class Unrelated {}");
        repo.Write("Unresolved/Unresolved.csproj", projectXml);
        repo.Write("Unresolved/Source.cs", "class App : Avalonia.Application { Missing lifetime; }");
        repo.Write("Unsupported/Unsupported.csproj", projectXml.Replace("../Avalonia.dll", "../Avalonia12.dll", StringComparison.Ordinal));
        repo.Write("Unsupported/Source.cs", "class App : Avalonia.Application {}");
        File.WriteAllBytes(Path.Combine(repo.Root, "Avalonia12.dll"), Assembly("Avalonia", "[assembly: System.Reflection.AssemblyVersion(\"12.0.0.0\")] public class Marker {}", references));
        repo.Commit();
        foreach (var project in Projects.Discover(repo.Root))
        {
            var restore = await Processes.Run("dotnet", ["restore", project, "--nologo"], repo.Root);
            Assert.Equal(0, restore.ExitCode);
        }
        foreach (var command in new[] { new[] { "repo", "describe" }, new[] { "dotnet", "inspect" } })
        {
            var result = await CommandTestRuntime.Execute(Cli.Parse(command), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
            Assert.Equal(0, result.ExitCode);
            var output = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            var rows = output["projects"]!.AsArray();
            Assert.Equal(4, rows.Count);
            var facts = rows.Single(item => item!["path"]!.GetValue<string>() == "App.csproj")!["frameworkCapabilities"]!.AsArray();
            Assert.Equal("static-lifetime-types:ISingleViewApplicationLifetime", facts.Single(item => item!["id"]!.GetValue<string>() == "avalonia-lifetime")!["evidence"]!.GetValue<string>());
            Assert.Equal("xaml", facts.Single(item => item!["id"]!.GetValue<string>() == "avalonia-structure")!["evidence"]!.GetValue<string>());
            Assert.Equal("detected", facts.Single(item => item!["id"]!.GetValue<string>() == "avalonia-themes")!["status"]!.GetValue<string>());
            Assert.Equal("detected", facts.Single(item => item!["id"]!.GetValue<string>() == "avalonia-compiled-bindings")!["status"]!.GetValue<string>());
            Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
            foreach (var row in rows.Where(item => item!["path"]!.GetValue<string>() != "App.csproj"))
            {
                var path = row!["path"]!.GetValue<string>();
                var expected = path.StartsWith("Negative/", StringComparison.Ordinal) ? "absent" : "unknown";
                foreach (var id in new[] { "avalonia-lifetime", "avalonia-structure" })
                    Assert.Equal(expected, row["frameworkCapabilities"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!["status"]!.GetValue<string>());
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(row["frameworkCapabilities"]).IsValid);
            }
            if (command[0] == "repo") Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json")).Evaluate(output).IsValid);
        }
    }

    static void Validate(FrameworkCapabilityFact[] facts) => Assert.True(JsonSchema.FromFile(
        Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
        .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
}
