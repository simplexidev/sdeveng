using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Hosting;

namespace SdevEng.Tests;

public sealed class FrameworkCapabilitiesTests
{
    [Theory]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var s = new ServiceCollection(); s.AddLogging();", "detected", 1)]
    [InlineData("using Microsoft.Extensions.Logging; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Logging.AddConsole().SetMinimumLevel(LogLevel.Debug);", "detected", 2)]
    [InlineData("using Microsoft.Extensions.Hosting; using Microsoft.Extensions.Logging; Host.CreateDefaultBuilder().ConfigureLogging(b => b.AddDebug());", "detected", 2)]
    [InlineData("using Microsoft.Extensions.Logging; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Logging.AddProvider(new Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider(new Microsoft.Extensions.Options.OptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(null!, null!, null!)));", "detected", 1)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var s = new ServiceCollection(); s.AddSingleton<ILoggerProvider, Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>();", "detected", 1)]
    [InlineData("System.Console.WriteLine(1);", "absent", 0)]
    [InlineData("var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();", "absent", 0)]
    [InlineData("using Microsoft.Extensions.Logging; ILogger logger = null!; logger.LogInformation(\"hello\");", "absent", 0)]
    [InlineData("Missing.AddLogging();", "unknown", 0)]
    [InlineData("dynamic b = new object(); b.AddConsole();", "unknown", 0)]
    [InlineData("typeof(object).GetMethod(\"AddLogging\")!.Invoke(null, null);", "unknown", 0)]
    [InlineData("class Logging { public static void AddLogging() {} } class Program { static void Main() { Logging.AddLogging(); } }", "unknown", 0)]
    public void LoggingCompositionUsesResolvedSymbols(string source, string status, int locations)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var fact = facts.Single(item => item.Id == "logging");
        Assert.Equal(status, fact.Status);
        Assert.Equal(locations, fact.Locations?.Length ?? 0);
        Assert.All(fact.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
        if (status == "detected") Assert.StartsWith("10.", fact.Version);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Logging", "using Microsoft.Extensions.DependencyInjection; var s = new ServiceCollection(); s.AddLogging();")]
    [InlineData("Microsoft.Extensions.Logging.Console", "using Microsoft.Extensions.Logging; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Logging.AddConsole();")]
    public void UnsupportedLoggingVersionsRemainUnknown(string package, string source)
    {
        var fact = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { [package] = "9.0.0" }).Single(item => item.Id == "logging");
        Assert.Equal("unknown", fact.Status);
        Assert.Equal("9.0.0", fact.Version);
        Assert.Equal("unsupported-microsoft-extensions-version", fact.Evidence);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == "logging").Status);
    }

    [Theory]
    [InlineData("using Microsoft.Extensions.Configuration; var c = new ConfigurationBuilder().Build(); var section = c.GetSection(\"App\"); section.Bind(new object()); var value = c.GetValue<int>(\"Count\");", "detected", "absent", 3, 0)]
    [InlineData("using Microsoft.Extensions.Configuration; var c = new ConfigurationBuilder().Build(); var value = c.Get<object>();", "detected", "absent", 1, 0)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Services.AddOptions<object>().Bind(b.Configuration);", "absent", "detected", 0, 2)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Services.Configure<object>(b.Configuration.GetSection(\"App\")); b.Services.AddOptions<object>().BindConfiguration(\"App\");", "detected", "detected", 1, 3)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); services.AddOptions<object>().Configure(o => {}); services.PostConfigure<object>(o => {});", "absent", "detected", 0, 3)]
    [InlineData("System.Console.WriteLine(1);", "absent", "absent", 0, 0)]
    [InlineData("Missing.Bind();", "unknown", "unknown", 0, 0)]
    [InlineData("dynamic c = new object(); c.Bind(new object());", "unknown", "unknown", 0, 0)]
    [InlineData("typeof(object).GetMethod(\"Bind\")!.Invoke(null, null);", "unknown", "unknown", 0, 0)]
    [InlineData("class Configuration { public static void Bind() {} } class Program { static void Main() { Configuration.Bind(); } }", "unknown", "unknown", 0, 0)]
    public void ConfigurationAndOptionsUseResolvedSymbols(string source, string configuration, string options, int configurationLocations, int optionsLocations)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        foreach (var (id, status, count) in new[] { ("configuration", configuration, configurationLocations), ("options", options, optionsLocations) })
        {
            var fact = facts.Single(item => item.Id == id);
            Assert.Equal(status, fact.Status);
            Assert.Equal(count, fact.Locations?.Length ?? 0);
            Assert.All(fact.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
        }
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Theory]
    [InlineData("configuration", "Microsoft.Extensions.Configuration.Abstractions")]
    [InlineData("options", "Microsoft.Extensions.Options")]
    public void UnsupportedConfigurationAndOptionsVersionsRemainUnknown(string id, string package)
    {
        var fact = FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"),
            new Dictionary<string, string?> { [package] = "9.0.0" }).Single(item => item.Id == id);
        Assert.Equal("unknown", fact.Status);
        Assert.Equal("9.0.0", fact.Version);
        Assert.Equal("unsupported-microsoft-extensions-version", fact.Evidence);
    }

    [Theory]
    [InlineData("configuration", "Microsoft.Extensions.Configuration.Binder", "using Microsoft.Extensions.Configuration; var c = new ConfigurationBuilder().Build(); c.Bind(new object());")]
    [InlineData("options", "Microsoft.Extensions.Options.ConfigurationExtensions", "using Microsoft.Extensions.DependencyInjection; var s = new ServiceCollection(); s.AddOptions<object>().BindConfiguration(\"App\");")]
    public void UnsupportedBindingPackageVersionsRemainUnknown(string id, string package, string source)
    {
        var fact = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { [package] = "9.0.0" }).Single(item => item.Id == id);
        Assert.Equal("unknown", fact.Status);
        Assert.Equal("9.0.0", fact.Version);
    }

    [Theory]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); b.Services.AddSingleton<object>(); b.Services.AddScoped<object>(); b.Services.AddTransient<object>();", "detected", "detected", 3)]
    [InlineData("var b = new Microsoft.Extensions.Hosting.HostBuilder();", "detected", "absent", 0)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; IServiceCollection services = new ServiceCollection(); services.Add(ServiceDescriptor.Singleton<object, object>());", "absent", "detected", 1)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.DependencyInjection.Extensions; var services = new ServiceCollection(); services.TryAddSingleton<object>(); services.AddKeyedScoped<object>(\"key\");", "absent", "detected", 2)]
    [InlineData("System.Console.WriteLine(1);", "absent", "absent", 0)]
    [InlineData("class Host { public static void CreateApplicationBuilder() {} } class Program { static void Main() { Host.CreateApplicationBuilder(); } }", "unknown", "unknown", 0)]
    [InlineData("Missing.Configure();", "unknown", "unknown", 0)]
    [InlineData("typeof(object).GetMethod(\"Configure\")!.Invoke(null, null);", "unknown", "unknown", 0)]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var a = new ServiceCollection(); a.AddSingleton<object>(); var b = new ServiceCollection(); b.AddTransient<object>(); a.BuildServiceProvider(); b.BuildServiceProvider();", "absent", "detected", 2)]
    public void HostingAndRegistrationsUseResolvedSymbols(string source, string host, string di, int registrations)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        Assert.Equal(host, facts.Single(fact => fact.Id == "generic-host").Status);
        var registration = facts.Single(fact => fact.Id == "dependency-injection");
        Assert.Equal(di, registration.Status);
        Assert.Equal(registrations, registration.Locations?.Length ?? 0);
        Assert.All(registration.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void UnsupportedHostingVersionsRemainUnknownAndLegacyFactsStayValid()
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile("var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();"),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Hosting"] = "9.0.0" });
        var host = facts.Single(fact => fact.Id == "generic-host");
        Assert.Equal("unknown", host.Status);
        Assert.Equal("9.0.0", host.Version);
        Assert.Equal("unsupported-microsoft-extensions-version", host.Evidence);
        foreach (var count in new[] { 4, 6, 8 })
            Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
                .Evaluate(JsonSerializer.SerializeToNode(facts.Take(count), AgentTool.Json)).IsValid);
    }

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
        Assert.Equal(new[] { plain, host, "unknown", "absent" }, facts.Take(4).Select(fact => fact.Status));
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
        Assert.Equal(new[] { "absent", "absent", "absent", "absent" }, FrameworkCapabilities.Detect("Library", Compile("System.Console.WriteLine(1);")).Take(4).Select(fact => fact.Status));
        Assert.Equal(new[] { "unknown", "unknown", "unknown", "absent" }, FrameworkCapabilities.Detect("Exe", null).Take(4).Select(fact => fact.Status));
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
                facts.Take(4).Select(fact => fact.Status));
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
        repo.Write("Hosted/Program.cs", "using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); builder.Services.AddSingleton<object>(); builder.Services.Configure<object>(builder.Configuration.GetSection(\"App\")); builder.Logging.AddConsole();");
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
                Assert.Equal(expected[row["path"]!.GetValue<string>()], facts.AsArray().Take(4).Select(fact => fact!["status"]!.GetValue<string>()));
                Assert.Equal(new[] { "plain-console", "generic-host-console", "console-redirection", "system-commandline" }, facts.AsArray().Take(4).Select(fact => fact!["id"]!.GetValue<string>()));
                if (row["path"]!.GetValue<string>() == "CommandLine/CommandLine.csproj")
                    Assert.Equal("2.0.0", facts[3]!["version"]!.GetValue<string>());
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
                Assert.Equal(9, facts.AsArray().Count);
                var path = row["path"]!.GetValue<string>();
                var hosted = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "generic-host")!;
                var di = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "dependency-injection")!;
                Assert.Equal(path == "Hosted/Hosted.csproj" ? "detected" : path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent", hosted["status"]!.GetValue<string>());
                Assert.Equal(hosted["status"]!.GetValue<string>(), di["status"]!.GetValue<string>());
                foreach (var id in new[] { "configuration", "options", "logging" })
                {
                    var fact = facts.AsArray().Single(item => item!["id"]!.GetValue<string>() == id)!;
                    Assert.Equal(hosted["status"]!.GetValue<string>(), fact["status"]!.GetValue<string>());
                    if (path == "Hosted/Hosted.csproj")
                    {
                        Assert.StartsWith("10.", fact["version"]!.GetValue<string>());
                        Assert.EndsWith("Hosted/Program.cs", fact["locations"]![0]!["path"]!.GetValue<string>());
                    }
                }
                if (path == "Hosted/Hosted.csproj")
                {
                    Assert.Equal("10.0.12", hosted["version"]!.GetValue<string>());
                    Assert.EndsWith("Hosted/Program.cs", di["locations"]![0]!["path"]!.GetValue<string>());
                }
            }
            if (command[0] == "repo")
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json")).Evaluate(json).IsValid);
        }
    }
}
