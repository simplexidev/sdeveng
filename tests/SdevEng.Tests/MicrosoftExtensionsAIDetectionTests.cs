using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SdevEng.Tests;

public sealed class MicrosoftExtensionsAIDetectionTests
{
    // Synthetic, offline client; detection compiles but never invokes it.
    const string Fake = """
        class Fake : Microsoft.Extensions.AI.IChatClient {
          public void Dispose() {}
          public object? GetService(System.Type type, object? key = null) => null;
          public System.Threading.Tasks.Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, System.Threading.CancellationToken token = default) => null!;
          public System.Collections.Generic.IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            System.Collections.Generic.IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, System.Threading.CancellationToken token = default) => null!;
        }
        """;

    static CSharpCompilation Compile(string source, bool references = true)
        => CSharpCompilation.Create("AIApplicationFixture", [CSharpSyntaxTree.ParseText(source, path: "Program.cs")],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(Microsoft.Extensions.AI.IChatClient).Assembly.Location)
                .Append(typeof(Microsoft.Extensions.DependencyInjection.ChatClientBuilderServiceCollectionExtensions).Assembly.Location)
                .Where(path => references || !Path.GetFileName(path).StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal))
                .Distinct().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

    [Fact]
    public async Task FixturesProveStaticFactsSchemaAndConditionalReferenceLoading()
    {
        var root = AgentTool.FindToolkit();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evals/microsoft-extensions-ai/fixtures/cases.json")))!;
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/microsoft-extensions-ai-fixtures.schema.json")).Evaluate(fixture).IsValid);
        foreach (var row in fixture["cases"]!.AsArray())
        {
            var source = row!["source"]!.GetValue<string>();
            var references = row["references"]!.GetValue<bool>();
            var compilation = Compile(source + (references ? Fake : ""), references);
            if (row["registration"]!.GetValue<string>() != "unknown")
                Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var packages = row["packages"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>());
            var facts = FrameworkCapabilities.Detect("Exe", compilation, packages);
            var package = facts.Single(fact => fact.Id == "microsoft-extensions-ai");
            var registration = facts.Single(fact => fact.Id == "chat-client-registration");
            var composition = facts.Single(fact => fact.Id == "chat-client-composition");
            if (row["composition"] is { } expectedComposition)
            {
                Assert.Equal(expectedComposition.GetValue<string>(), composition.Status);
                Assert.Equal(row["compositionLocations"]!.GetValue<int>(), composition.Locations?.Length ?? 0);
            }
            Assert.Equal(row["package"]!.GetValue<string>(), package.Status);
            Assert.Equal(row["registration"]!.GetValue<string>(), registration.Status);
            Assert.Equal(row["version"]?.GetValue<string>(), package.Version);
            Assert.Equal(row["locations"]!.GetValue<int>(), registration.Locations?.Length ?? 0);
            Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/framework-capabilities.schema.json"))
                .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
            var context = new SkillActivationContext("coder", [], [], [],
                new[] { new SkillFrameworkFact("microsoft-extensions", "10.0.0") }
                .Concat(new[] { package, registration, composition }.Where(fact => fact.Status == "detected")
                    .Select(fact => new SkillFrameworkFact(fact.Id, fact.Version!))).ToArray());
            var service = new SkillActivationService();
            var loaded = await service.LoadReferenceAsync(root, context, "microsoft-extensions", "references/ai.md");
            Assert.Equal(registration.Status == "detected" ? "loaded" : "omitted", loaded.Status);
            if (loaded.Status == "loaded") Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/microsoft-extensions/references/ai.md")), loaded.Content);
        }
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(fact => fact.Id == "chat-client-registration").Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(fact => fact.Id == "chat-client-composition").Status);
    }

    [Theory]
    [InlineData("services.AddChatClient(new Fake());")]
    [InlineData("services.AddChatClient(_ => new Fake());")]
    [InlineData("services.AddKeyedChatClient(\"offline\", new Fake());")]
    [InlineData("services.AddSingleton<Microsoft.Extensions.AI.IChatClient, Fake>();")]
    [InlineData("services.AddScoped<Microsoft.Extensions.AI.IChatClient>(_ => new Fake());")]
    [InlineData("services.AddTransient<Microsoft.Extensions.AI.IChatClient>(newClient => new Fake());")]
    [InlineData("services.TryAddSingleton<Microsoft.Extensions.AI.IChatClient, Fake>();")]
    [InlineData("services.AddKeyedSingleton<Microsoft.Extensions.AI.IChatClient, Fake>(\"offline\");")]
    [InlineData("services.AddSingleton(typeof(Microsoft.Extensions.AI.IChatClient), typeof(Fake));")]
    [InlineData("services.TryAddScoped(typeof(Microsoft.Extensions.AI.IChatClient), typeof(Fake));")]
    public void RegistrationsRequireResolvedServiceIdentity(string registration)
    {
        var compilation = Compile("using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.DependencyInjection.Extensions; var services = new ServiceCollection(); " + registration + Fake);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var fact = FrameworkCapabilities.Detect("Exe", compilation).Single(fact => fact.Id == "chat-client-registration");
        Assert.Equal("detected", fact.Status);
        Assert.Equal("Program.cs", Assert.Single(fact.Locations!).Path);
    }

    [Fact]
    public async Task NormalDiscoveryExposesAIRegistrationAndPackageVersions()
    {
        using var repo = new TemporaryGitRepository();
        foreach (var (name, source) in new[] {
            ("Registered", "using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.AI; var services = new ServiceCollection(); services.AddChatClient(new Fake()).UseLogging();" + Fake),
            ("Unused", "System.Console.WriteLine(1);"),
            ("Unresolved", "Missing.ConfigureChatClient();") })
        {
            repo.Write(name + "/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                <ItemGroup><PackageReference Include="Microsoft.Extensions.AI" Version="10.0.0" /><PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" /></ItemGroup></Project>
                """);
            repo.Write(name + "/Program.cs", source);
        }
        repo.Commit();
        foreach (var project in Projects.Discover(repo.Root))
            Assert.Equal(0, (await Processes.Run("dotnet", ["restore", project, "--nologo"], repo.Root)).ExitCode);
        foreach (var command in new[] { new[] { "repo", "describe" }, new[] { "dotnet", "inspect" } })
        {
            var result = await CommandTestRuntime.Execute(Cli.Parse(command), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
            var json = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            Assert.Equal(3, json["projects"]!.AsArray().Count);
            foreach (var row in json["projects"]!.AsArray())
            {
                var facts = row!["frameworkCapabilities"]!;
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
                var package = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "microsoft-extensions-ai")!;
                Assert.Equal("detected", package["status"]!.GetValue<string>());
                Assert.Equal("10.0.0", package["version"]!.GetValue<string>());
                var expected = row["path"]!.GetValue<string>().Split('/')[0] switch { "Registered" => "detected", "Unused" => "absent", _ => "unknown" };
                var registration = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "chat-client-registration")!;
                Assert.Equal(expected, registration["status"]!.GetValue<string>());
                var composition = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "chat-client-composition")!;
                Assert.Equal(expected, composition["status"]!.GetValue<string>());
                if (expected == "detected") Assert.EndsWith("Registered/Program.cs", composition["locations"]![0]!["path"]!.GetValue<string>());
                if (expected == "detected") Assert.EndsWith("Registered/Program.cs", registration["locations"]![0]!["path"]!.GetValue<string>());
            }
            if (command[0] == "repo") Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/repository-description.schema.json")).Evaluate(json).IsValid);
        }
    }
}
