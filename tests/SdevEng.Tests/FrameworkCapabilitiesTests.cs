using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Caching.Memory;

namespace SdevEng.Tests;

public sealed class FrameworkCapabilitiesTests
{
    const string Worker = "class Worker : Microsoft.Extensions.Hosting.BackgroundService { protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken) => System.Threading.Tasks.Task.Delay(-1, stoppingToken); }";

    [Theory]
    [InlineData("_ = System.Type.GetType(\"Widget\");", "detected", 1)]
    [InlineData("_ = typeof(string).GetMethods(); _ = System.Activator.CreateInstance(typeof(object));", "detected", 2)]
    [InlineData("_ = System.Reflection.Assembly.LoadFrom(\"plugin.dll\"); _ = typeof(System.Collections.Generic.List<>).MakeGenericType(typeof(int));", "detected", 2)]
    [InlineData("Fake.GetMethods(); class Fake { public static void GetMethods() {} }", "absent", 0)]
    [InlineData("System.Console.WriteLine(1);", "absent", 0)]
    [InlineData("Missing.GetMethods();", "unknown", 0)]
    public void ReflectionFactsAreLimitedResolvedCandidates(string source, string status, int count)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var fact = facts.Single(item => item.Id == "reflection-sensitive");
        Assert.Equal(status, fact.Status);
        Assert.Contains("limited-analysis", fact.Evidence);
        Assert.Equal(count, fact.Locations?.Length ?? 0);
        Assert.All(fact.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
        Assert.Equal("unknown", FrameworkCapabilities.Detect(null, null).Single(item => item.Id == fact.Id).Status);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public async Task TrimmingReferenceRequiresDetectedEvaluatedSetting()
    {
        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        const string path = "references/trimming.md";
        foreach (var value in new[] { "true", "false", "$(Unset)", "" })
        {
            var facts = FrameworkCapabilities.Detect(null, null, projectProperties: new Dictionary<string, string?> { ["PublishTrimmed"] = value });
            var context = new SkillActivationContext("coder", ["public-api-change"], [], [],
                facts.Where(item => item.Status == "detected").Select(item => new SkillFrameworkFact(item.Id, item.Version ?? "1.0.0")).ToArray());
            var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "api-compatibility");
            Assert.Equal("publish-trimmed", Assert.Single(Assert.Single(metadata.Resources!, item => item.Path == path).Activation!).Id);
            var loaded = await service.LoadReferenceAsync(root, context, "api-compatibility", path);
            Assert.Equal(value == "true" ? "loaded" : "omitted", loaded.Status);
            if (value == "true")
            {
                Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/api-compatibility", path)), loaded.Content);
                Assert.Contains(service.Activate(root, context with { RequestedCapabilities = [] }), item => item.Id == "api-compatibility");
            }
            else Assert.Equal("resource-not-activated", loaded.OmissionReason);
            SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
        }
    }

    [Fact]
    public async Task NativeAotReferenceRequiresDetectedEvaluatedSetting()
    {
        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        const string path = "references/native-aot.md";
        foreach (var value in new[] { "true", "false", "$(Unset)", "" })
        {
            var facts = FrameworkCapabilities.Detect(null, null, projectProperties: new Dictionary<string, string?> { ["PublishAot"] = value });
            var context = new SkillActivationContext("coder", ["public-api-change"], [], [],
                facts.Where(item => item.Status == "detected").Select(item => new SkillFrameworkFact(item.Id, item.Version ?? "1.0.0")).ToArray());
            var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "api-compatibility");
            Assert.Equal("publish-aot", Assert.Single(Assert.Single(metadata.Resources!, item => item.Path == path).Activation!).Id);
            var loaded = await service.LoadReferenceAsync(root, context, "api-compatibility", path);
            Assert.Equal(value == "true" ? "loaded" : "omitted", loaded.Status);
            if (value == "true") Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/api-compatibility", path)), loaded.Content);
            else Assert.Equal("resource-not-activated", loaded.OmissionReason);
            SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
        }
    }

    [Fact]
    public void EvaluatedPublishSettingsDistinguishEnabledDisabledAndUnavailable()
    {
        var enabled = FrameworkCapabilities.Detect(null, null, projectProperties: new Dictionary<string, string?>
        { ["PublishTrimmed"] = "true", ["PublishAot"] = "true", ["TargetFramework"] = "net10.0", ["RuntimeIdentifier"] = "linux-x64" });
        Assert.Equal("detected", enabled.Single(item => item.Id == "publish-trimmed").Status);
        Assert.Contains("net10.0;linux-x64", enabled.Single(item => item.Id == "publish-aot").Evidence);
        var disabled = FrameworkCapabilities.Detect(null, null, projectProperties: new Dictionary<string, string?>
        { ["PublishTrimmed"] = "false", ["PublishAot"] = "false" });
        Assert.All(disabled.Where(item => item.Id is "publish-trimmed" or "publish-aot"), item => Assert.Equal("absent", item.Status));
        var unresolved = FrameworkCapabilities.Detect(null, null, projectProperties: new Dictionary<string, string?>
        { ["PublishTrimmed"] = "$(Unset)", ["PublishAot"] = "" });
        Assert.All(unresolved.Where(item => item.Id is "publish-trimmed" or "publish-aot"), item => Assert.Equal("unknown", item.Status));
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(enabled, AgentTool.Json)).IsValid);
    }

    [Theory]
    [InlineData("using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; using OpenTelemetry; var s = new ServiceCollection(); s.AddLogging(l => l.AddOpenTelemetry()); s.AddOpenTelemetry().WithTracing(t => {}).WithMetrics(m => {});", "detected")]
    [InlineData("System.Console.WriteLine(1);", "absent")]
    [InlineData("Missing.WithTracing();", "unknown")]
    public void TelemetryCompositionRequiresResolvedSymbols(string source, string status)
    {
        var packages = new Dictionary<string, string?> { ["OpenTelemetry"] = "1.9.0", ["OpenTelemetry.Extensions.Hosting"] = "1.9.0" };
        var compilation = Compile(source, telemetry: true);
        var facts = FrameworkCapabilities.Detect("Exe", compilation, packages);
        foreach (var id in new[] { "telemetry-logging", "telemetry-tracing", "telemetry-metrics" })
        {
            var fact = facts.Single(item => item.Id == id);
            Assert.Equal((id, status), (fact.Id, fact.Status));
            Assert.Equal("1.9.0", fact.Version);
            Assert.Equal(status == "detected" ? 1 : 0, fact.Locations?.Length ?? 0);
            Assert.All(fact.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
            Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null, packages).Single(item => item.Id == id).Status);
            Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", Compile(source),
                new Dictionary<string, string?> { ["OpenTelemetry"] = "2.0.0", ["OpenTelemetry.Extensions.Hosting"] = "2.0.0" })
                .Single(item => item.Id == id).Status);
        }
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public async Task CachingReferenceIsDiscoveredAndLoadedOnlyForMatchingFacts()
    {
        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var context = new SkillActivationContext("coder", [], [], [], [new("microsoft-extensions", "10.0.0"), new("memory-cache", "10.0.0")]);
        var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "microsoft-extensions");
        Assert.Contains(metadata.Resources!, item => item.Path == "references/caching.md");
        var loaded = await service.LoadReferenceAsync(root, context, "microsoft-extensions", "references/caching.md");
        Assert.Equal("loaded", loaded.Status);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/microsoft-extensions/references/caching.md")), loaded.Content);
        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            context with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, "microsoft-extensions", "references/caching.md")).OmissionReason);
        foreach (var version in new[] { "10.0.0", "9.0.0" })
        {
            var facts = FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"),
                new Dictionary<string, string?> { ["Microsoft.Extensions.Caching.Memory"] = version });
            var cache = facts.Single(item => item.Id == "memory-cache");
            Assert.Equal(version == "10.0.0" ? "absent" : "unknown", cache.Status);
            var unmatched = context with
            {
                Frameworks = new[] { new SkillFrameworkFact("microsoft-extensions", "10.0.0") }
                .Concat(facts.Where(item => item.Id == "memory-cache" && item.Status == "detected")
                    .Select(item => new SkillFrameworkFact(item.Id, item.Version!))).ToArray()
            };
            Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root, unmatched,
                "microsoft-extensions", "references/caching.md")).OmissionReason);
        }
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
    }

    [Fact]
    public async Task ChannelsFactAndReferenceRequireResolvedChannelUse()
    {
        var source = "var channel = System.Threading.Channels.Channel.CreateUnbounded<int>();";
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var channels = facts.Single(item => item.Id == "channels");
        Assert.Equal("detected", channels.Status);
        Assert.Equal("roslyn-resolved-api-use", channels.Evidence);
        Assert.Single(channels.Locations!);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
        Assert.Equal("absent", FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"))
            .Single(item => item.Id == "channels").Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == "channels").Status);

        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var context = new SkillActivationContext("coder", [], [], [], [new("microsoft-extensions", "10.0.0"), new("channels", "1.0.0")]);
        var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "microsoft-extensions");
        const string path = "references/channels.md";
        Assert.Contains(metadata.Resources!, item => item.Path == path && Assert.Single(item.Activation!).Id == "channels");
        Assert.Equal("loaded", (await service.LoadReferenceAsync(root, context, "microsoft-extensions", path)).Status);
        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            context with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, "microsoft-extensions", path)).OmissionReason);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
    }

    [Fact]
    public async Task LocalizationFactAndReferenceRequireResolvedRegistration()
    {
        var source = "using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); services.AddLocalization();";
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var localization = facts.Single(item => item.Id == "localization");
        Assert.Equal("detected", localization.Status);
        Assert.Equal("roslyn-resolved-api-use", localization.Evidence);
        Assert.Single(localization.Locations!);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
        Assert.Equal("absent", FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"))
            .Single(item => item.Id == "localization").Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == "localization").Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", Compile("Missing.AddLocalization();"))
            .Single(item => item.Id == "localization").Status);

        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var context = new SkillActivationContext("coder", [], [], [], [new("microsoft-extensions", "10.0.0"), new("localization", "1.0.0")]);
        var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "microsoft-extensions");
        const string path = "references/localization.md";
        Assert.Contains(metadata.Resources!, item => item.Path == path && Assert.Single(item.Activation!).Id == "localization");
        var loaded = await service.LoadReferenceAsync(root, context, "microsoft-extensions", path);
        Assert.Equal("loaded", loaded.Status);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/microsoft-extensions", path)), loaded.Content);
        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            context with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, "microsoft-extensions", path)).OmissionReason);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
    }

    [Fact]
    public async Task SecretsConfigurationReferenceRequiresResolvedConfigurationUse()
    {
        var source = "using Microsoft.Extensions.Configuration; var configuration = new ConfigurationBuilder().Build(); configuration.GetSection(\"Service\");";
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var configuration = facts.Single(item => item.Id == "configuration");
        Assert.Equal("detected", configuration.Status);
        Assert.Equal("roslyn-static-composition", configuration.Evidence);
        Assert.Single(configuration.Locations!);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);

        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var context = new SkillActivationContext("coder", [], [], [], [new("microsoft-extensions", "10.0.0"), new("configuration", "10.0.0")]);
        var metadata = Assert.Single(service.Activate(root, context), item => item.Id == "microsoft-extensions");
        const string path = "references/secrets-configuration.md";
        Assert.Contains(metadata.Resources!, item => item.Path == path && Assert.Single(item.Activation!).Id == "configuration");
        var loaded = await service.LoadReferenceAsync(root, context, "microsoft-extensions", path);
        Assert.Equal("loaded", loaded.Status);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/microsoft-extensions", path)), loaded.Content);
        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            context with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, "microsoft-extensions", path)).OmissionReason);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
    }

    [Fact]
    public async Task TelemetryReferenceRequiresDetectedCompositionAndIsHashValidated()
    {
        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var skill = "microsoft-extensions";
        var path = "references/telemetry.md";
        var composed = new SkillActivationContext("coder", [], [], [],
        [new("microsoft-extensions", "10.0.0"), new("telemetry-logging", "1.9.0")]);
        var metadata = Assert.Single(service.Activate(root, composed), item => item.Id == skill);
        Assert.Contains(metadata.Resources!, item => item.Path == path && Assert.Single(item.Activation!).Id == "telemetry-logging");
        Assert.Equal("loaded", (await service.LoadReferenceAsync(root, composed, skill, path)).Status);

        foreach (var frameworks in new[]
        {
            new[] { new SkillFrameworkFact("microsoft-extensions", "10.0.0"), new SkillFrameworkFact("opentelemetry", "1.9.0") },
            new[] { new SkillFrameworkFact("microsoft-extensions", "10.0.0"), new SkillFrameworkFact("telemetry-tracing", "2.0.0") }
        })
            Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
                composed with { Frameworks = frameworks }, skill, path)).OmissionReason);

        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            composed with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, skill, path)).OmissionReason);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/skills/microsoft-extensions", path)),
            (await service.LoadReferenceAsync(root, composed, skill, path)).Content);
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/skill-metadata.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(metadata, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!).IsValid);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
    }

    [Theory]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); services.AddHttpClient();", "detected", 1)]
    [InlineData("System.Console.WriteLine(1);", "absent", 0)]
    [InlineData("Missing.AddHttpClient();", "unknown", 0)]
    [InlineData("dynamic services = new object(); services.AddHttpClient();", "unknown", 0)]
    public void HttpClientFactoryUsesResolvedRegistrationSymbols(string source, string status, int locations)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        var fact = facts.Single(item => item.Id == "http-client-factory");
        Assert.Equal(status, fact.Status);
        Assert.Equal(locations, fact.Locations?.Length ?? 0);
        Assert.All(fact.Locations ?? [], location => { Assert.Equal(1, location.Line); Assert.True(location.Column > 0); });
        if (status == "detected") Assert.StartsWith("10.", fact.Version);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void HttpClientFactoryRejectsUnsupportedAndUnavailableEvidence()
    {
        var source = "using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); services.AddHttpClient();";
        var unsupported = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Http"] = "9.0.0" }).Single(item => item.Id == "http-client-factory");
        Assert.Equal("unknown", unsupported.Status);
        Assert.Equal("unsupported-microsoft-extensions-version", unsupported.Evidence);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == "http-client-factory").Status);
    }

    [Theory]
    [InlineData("using Microsoft.Extensions.DependencyInjection; var s = new ServiceCollection(); s.AddMemoryCache(); s.AddDistributedMemoryCache();", "detected", "detected")]
    [InlineData("System.Console.WriteLine(1);", "absent", "absent")]
    [InlineData("Missing.AddMemoryCache(); Missing.AddDistributedMemoryCache();", "unknown", "unknown")]
    public void CacheRegistrationsRequireResolvedRoslynSymbols(string source, string memory, string distributed)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Caching.Memory"] = "10.0.0" });
        Assert.Equal(memory, facts.Single(fact => fact.Id == "memory-cache").Status);
        Assert.Equal(distributed, facts.Single(fact => fact.Id == "distributed-cache").Status);
        foreach (var id in new[] { "memory-cache", "distributed-cache" })
        {
            var fact = facts.Single(item => item.Id == id);
            Assert.Equal(memory == "detected" ? 1 : 0, fact.Locations?.Length ?? 0);
            Assert.All(fact.Locations ?? [], location => Assert.Equal(1, location.Line));
        }
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(fact => fact.Id == "memory-cache").Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", Compile("System.Console.WriteLine(1);"),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Caching.Memory"] = "9.0.0" })
            .Single(fact => fact.Id == "distributed-cache").Status);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void TelemetryFactsDetectResolvedPackagesWithoutClaimingConfiguration()
    {
        var source = "System.Console.WriteLine(1);";
        var installed = FrameworkCapabilities.Detect("Exe", Compile(source), new Dictionary<string, string?>
        { ["Microsoft.Extensions.Telemetry"] = "10.0.0", ["OpenTelemetry.Exporter.Console"] = "1.9.0" });
        foreach (var (id, version) in new[] { ("microsoft-extensions-telemetry", "10.0.0"), ("opentelemetry", "1.9.0") })
        {
            var fact = installed.Single(item => item.Id == id);
            Assert.Equal(("detected", "resolved-package", version), (fact.Status, fact.Evidence, fact.Version));
            Assert.Empty(fact.Locations ?? []);
        }
        var absent = FrameworkCapabilities.Detect("Exe", Compile(source));
        Assert.Equal("absent", absent.Single(item => item.Id == "microsoft-extensions-telemetry").Status);
        Assert.Equal("absent", absent.Single(item => item.Id == "opentelemetry").Status);
        var unsupported = FrameworkCapabilities.Detect("Exe", Compile(source), new Dictionary<string, string?>
        { ["Microsoft.Extensions.Telemetry"] = "9.0.0", ["OpenTelemetry"] = "2.0.0" });
        foreach (var id in new[] { "microsoft-extensions-telemetry", "opentelemetry" })
        {
            Assert.Equal("unknown", unsupported.Single(item => item.Id == id).Status);
            Assert.Equal("unsupported-package-version", unsupported.Single(item => item.Id == id).Evidence);
        }
        Assert.All(FrameworkCapabilities.Detect("Exe", null), fact =>
        {
            if (fact.Id is "microsoft-extensions-telemetry" or "opentelemetry") Assert.Equal("unknown", fact.Status);
        });
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(installed, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void HttpClientFactoryDistinguishesTypedAndNamedRegistrations()
    {
        var source = "using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); services.AddHttpClient<WidgetClient>(); services.AddHttpClient(\"catalog\", client => {}); class WidgetClient : System.Net.Http.HttpClient {}";
        var facts = FrameworkCapabilities.Detect("Exe", Compile(source));
        Assert.Equal(("detected", 2), (facts.Single(item => item.Id == "http-client-factory").Status, facts.Single(item => item.Id == "http-client-factory").Locations?.Length ?? 0));
        Assert.Equal(("detected", 1), (facts.Single(item => item.Id == "http-client-typed").Status, facts.Single(item => item.Id == "http-client-typed").Locations?.Length ?? 0));
        Assert.Equal(("detected", 1), (facts.Single(item => item.Id == "http-client-named").Status, facts.Single(item => item.Id == "http-client-named").Locations?.Length ?? 0));
        var plain = FrameworkCapabilities.Detect("Exe", Compile("var client = new System.Net.Http.HttpClient();"));
        Assert.Equal("absent", plain.Single(item => item.Id == "http-client-typed").Status);
        Assert.Equal("absent", plain.Single(item => item.Id == "http-client-named").Status);
        var unresolved = FrameworkCapabilities.Detect("Exe", Compile("Missing.AddHttpClient<WidgetClient>(); class WidgetClient {}"));
        Assert.Equal("unknown", unresolved.Single(item => item.Id == "http-client-typed").Status);
        Assert.Equal("unknown", unresolved.Single(item => item.Id == "http-client-named").Status);
    }

    [Fact]
    public async Task HttpResilienceRequiresSupportedResolvedPackageAndGatesItsReference()
    {
        var source = "System.Console.WriteLine(1);";
        var supported = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Http.Resilience"] = "10.0.0" });
        var fact = supported.Single(item => item.Id == "http-client-resilience");
        Assert.Equal(("detected", "resolved-package", "10.0.0"), (fact.Status, fact.Evidence, fact.Version));
        Assert.Equal("absent", FrameworkCapabilities.Detect("Exe", Compile(source)).Single(item => item.Id == "http-client-resilience").Status);
        var unsupported = FrameworkCapabilities.Detect("Exe", Compile(source),
            new Dictionary<string, string?> { ["Microsoft.Extensions.Http.Resilience"] = "9.0.0" }).Single(item => item.Id == "http-client-resilience");
        Assert.Equal("unknown", unsupported.Status);
        Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == "http-client-resilience").Status);
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(supported, AgentTool.Json)).IsValid);

        var root = AgentTool.FindToolkit();
        var service = new SkillActivationService();
        var skill = "microsoft-extensions";
        var path = "references/http-client-resilience.md";
        var context = new SkillActivationContext("coder", [], [], [], [new("microsoft-extensions", "10.0.0"), new("http-client-resilience", "10.0.0")]);
        Assert.Equal("loaded", (await service.LoadReferenceAsync(root, context, skill, path)).Status);
        Assert.Equal("resource-not-activated", (await service.LoadReferenceAsync(root,
            context with { Frameworks = [new("microsoft-extensions", "10.0.0")] }, skill, path)).OmissionReason);
        Assert.Equal("unknown-reference", (await service.LoadReferenceAsync(root, context, skill, "missing.md")).OmissionReason);
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/skill-metadata.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(SkillCompatibilityMapReader.ReadMetadataIndex(root).Single(item => item.Id == skill), new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!).IsValid);
        SkillCompatibilityMapReader.ValidateResources(root, [SkillCompatibilityMapReader.ReadMetadataIndex(root).Single(item => item.Id == skill)]);
    }

    [Theory]
    [InlineData("services.AddHostedService<Worker>();", "detected", "detected")]
    [InlineData("services.AddHostedService<Worker>(s => new Worker());", "detected", "detected")]
    [InlineData("services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, Worker>();", "detected", "detected")]
    [InlineData("services.AddSingleton<Worker>();", "absent", "absent")]
    [InlineData("", "absent", "absent")]
    [InlineData("Missing.AddHostedService<Worker>();", "unknown", "unknown")]
    [InlineData("dynamic d = services; d.AddHostedService<Worker>();", "unknown", "unknown")]
    public void HostedServicesRequireResolvedIdentityAndRegistration(string registration, string background, string hosted)
    {
        var facts = FrameworkCapabilities.Detect("Exe", Compile("using Microsoft.Extensions.DependencyInjection; var services = new ServiceCollection(); " + registration + Worker));
        Assert.Equal(background, facts.Single(fact => fact.Id == "background-service").Status);
        Assert.Equal(hosted, facts.Single(fact => fact.Id == "hosted-service").Status);
        Assert.Equal(hosted == "detected" ? 1 : 0, facts.Single(fact => fact.Id == "hosted-service").Locations?.Length ?? 0);
        Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json"))
            .Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void HostedServicesRejectUnsupportedVersionsAndUnavailableCompilation()
    {
        var source = "using Microsoft.Extensions.DependencyInjection; var s = new ServiceCollection(); s.AddHostedService<Worker>(); " + Worker;
        foreach (var id in new[] { "background-service", "hosted-service" })
        {
            var fact = FrameworkCapabilities.Detect("Exe", Compile(source), new Dictionary<string, string?>
            { ["Microsoft.Extensions.Hosting.Abstractions"] = "9.0.0" }).Single(item => item.Id == id);
            Assert.Equal("unknown", fact.Status);
            Assert.Equal("unsupported-microsoft-extensions-version", fact.Evidence);
            Assert.Equal("unknown", FrameworkCapabilities.Detect("Exe", null).Single(item => item.Id == id).Status);
        }
    }
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
    public void HostingFixturesUseResolvedSymbolsAndValidateContract()
    {
        var root = AgentTool.FindToolkit();
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evals/hosting-detection/fixtures/cases.json")))!;
        Assert.True(JsonSchema.FromFile(Path.Combine(root, "schemas/hosting-detection-fixtures.schema.json")).Evaluate(fixtures).IsValid);
        var capabilitySchema = JsonSchema.FromFile(Path.Combine(root, "schemas/framework-capabilities.schema.json"));
        foreach (var item in fixtures["cases"]!.AsArray())
        {
            var packages = item!["packageVersions"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>());
            var facts = FrameworkCapabilities.Detect("Exe", Compile(item["source"]!.GetValue<string>(), telemetry: item["id"]!.GetValue<string>().StartsWith("telemetry-static", StringComparison.Ordinal)), packages);
            foreach (var pair in item["expected"]!.AsObject())
                Assert.Equal(pair.Value!.GetValue<string>(), facts.Single(fact => fact.Id == pair.Key).Status);
            foreach (var fact in facts.Where(fact => item["expected"]![fact.Id] is not null))
                Assert.Equal(item["expectedLocations"]!.AsObject().TryGetPropertyValue(fact.Id, out var count)
                    ? count!.GetValue<int>() : 0, fact.Locations?.Length ?? 0);
            if (item["expectedVersions"] is JsonObject versions)
                foreach (var pair in versions)
                    Assert.Equal(pair.Value?.GetValue<string>(), facts.Single(fact => fact.Id == pair.Key).Version);
            Assert.True(capabilitySchema.Evaluate(JsonSerializer.SerializeToNode(facts, AgentTool.Json)).IsValid);
        }
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

    static CSharpCompilation Compile(string source, bool telemetry = false) => CSharpCompilation.Create("Fixture",
        [CSharpSyntaxTree.ParseText(source)],
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Host).Assembly.Location).Append(typeof(IHttpClientFactory).Assembly.Location)
            .Append(typeof(IMemoryCache).Assembly.Location)
            .Append(typeof(Microsoft.Extensions.DependencyInjection.LocalizationServiceCollectionExtensions).Assembly.Location)
            .Append(typeof(OpenTelemetry.OpenTelemetryBuilder).Assembly.Location)
            .Append(typeof(OpenTelemetry.Trace.TracerProviderBuilder).Assembly.Location)
            .Where(path => telemetry || !Path.GetFileName(path).StartsWith("OpenTelemetry", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal))
            .Distinct().Select(path => MetadataReference.CreateFromFile(path)),
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
        repo.Write("Program.cs", "System.Console.WriteLine(1); _ = typeof(string).GetMethods();");
        // Independent project fixtures, including installed-but-unused Hosting.
        repo.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><PublishTrimmed>true</PublishTrimmed><PublishAot>false</PublishAot><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>");
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.12\" /></ItemGroup></Project>";
        repo.Write("Hosted/Hosted.csproj", project.Replace("</Project>", "<ItemGroup><PackageReference Include=\"Microsoft.Extensions.Http\" Version=\"10.0.12\" /><PackageReference Include=\"Microsoft.Extensions.Caching.Memory\" Version=\"10.0.12\" /></ItemGroup></Project>", StringComparison.Ordinal));
        repo.Write("Hosted/Program.cs", "using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(); builder.Services.AddSingleton<object>(); builder.Services.Configure<object>(builder.Configuration.GetSection(\"App\")); builder.Logging.AddConsole(); builder.Services.AddHttpClient(); builder.Services.AddMemoryCache(); builder.Services.AddDistributedMemoryCache(); builder.Services.AddHostedService<Worker>(); " + Worker);
        repo.Write("Unused/Unused.csproj", project);
        repo.Write("Unused/Program.cs", "System.Console.WriteLine(1); " + Worker);
        repo.Write("CommandLine/CommandLine.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"System.CommandLine\" Version=\"2.0.0\" /></ItemGroup></Project>");
        repo.Write("CommandLine/Program.cs", "System.Console.WriteLine(1);");
        repo.Write("Broken/Broken.csproj", project.Replace("</PropertyGroup>", "<PublishTrimmed>$(Unset)</PublishTrimmed><PublishAot></PublishAot></PropertyGroup>", StringComparison.Ordinal));
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
                var path = row!["path"]!.GetValue<string>();
                var facts = row!["frameworkCapabilities"]!;
                Assert.Equal(expected[row["path"]!.GetValue<string>()], facts.AsArray().Take(4).Select(fact => fact!["status"]!.GetValue<string>()));
                Assert.Equal(new[] { "plain-console", "generic-host-console", "console-redirection", "system-commandline" }, facts.AsArray().Take(4).Select(fact => fact!["id"]!.GetValue<string>()));
                if (row["path"]!.GetValue<string>() == "CommandLine/CommandLine.csproj")
                    Assert.Equal("2.0.0", facts[3]!["version"]!.GetValue<string>());
                Assert.True(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/framework-capabilities.schema.json")).Evaluate(facts).IsValid);
                Assert.Equal(29, facts.AsArray().Count);
                foreach (var id in new[] { "microsoft-extensions-ai", "chat-client-registration" })
                    Assert.Equal(path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent",
                        facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == id)!["status"]!.GetValue<string>());
                if (path == "App.csproj")
                {
                    var reflection = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "reflection-sensitive")!;
                    Assert.Equal("detected", reflection["status"]!.GetValue<string>());
                    var location = Assert.Single(reflection["locations"]!.AsArray())!;
                    Assert.EndsWith("Program.cs", location["path"]!.GetValue<string>());
                    Assert.Equal(1, location["line"]!.GetValue<int>());
                    Assert.Equal("detected", facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "publish-trimmed")!["status"]!.GetValue<string>());
                    Assert.Equal("absent", facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "publish-aot")!["status"]!.GetValue<string>());
                }
                if (path == "Broken/Broken.csproj")
                    Assert.All(facts.AsArray().Where(fact => fact!["id"]!.GetValue<string>() is "publish-trimmed" or "publish-aot"), fact => Assert.Equal("unknown", fact!["status"]!.GetValue<string>()));
                Assert.Contains(facts.AsArray(), fact => fact!["id"]!.GetValue<string>() == "publish-trimmed");
                Assert.Contains(facts.AsArray(), fact => fact!["id"]!.GetValue<string>() == "publish-aot");
                Assert.Contains(facts.AsArray(), fact => fact!["id"]!.GetValue<string>() == "channels");
                Assert.Contains(facts.AsArray(), fact => fact!["id"]!.GetValue<string>() == "localization");
                foreach (var id in new[] { "memory-cache", "distributed-cache" })
                {
                    var cache = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == id)!;
                    Assert.Equal(path == "Hosted/Hosted.csproj" ? "detected" : path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent",
                        cache["status"]!.GetValue<string>());
                    if (path == "Hosted/Hosted.csproj")
                    {
                        Assert.Equal("10.0.12", cache["version"]!.GetValue<string>());
                        Assert.EndsWith("Hosted/Program.cs", cache["locations"]![0]!["path"]!.GetValue<string>());
                    }
                }
                var httpClientFactory = facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "http-client-factory")!;
                Assert.Equal(path == "Hosted/Hosted.csproj" ? "detected" : path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent",
                    httpClientFactory["status"]!.GetValue<string>());
                var expectedClientShape = path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent";
                Assert.Equal(expectedClientShape, facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "http-client-typed")!["status"]!.GetValue<string>());
                Assert.Equal(expectedClientShape, facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == "http-client-named")!["status"]!.GetValue<string>());
                if (path == "Hosted/Hosted.csproj")
                {
                    Assert.Equal("10.0.12", httpClientFactory["version"]!.GetValue<string>());
                    Assert.EndsWith("Hosted/Program.cs", httpClientFactory["locations"]![0]!["path"]!.GetValue<string>());
                }
                foreach (var id in new[] { "background-service", "hosted-service" })
                {
                    Assert.Equal(path == "Hosted/Hosted.csproj" ? "detected" : path is "Broken/Broken.csproj" or "Unsupported/Unsupported.vbproj" ? "unknown" : "absent",
                        facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == id)!["status"]!.GetValue<string>());
                    if (path == "Hosted/Hosted.csproj")
                        Assert.EndsWith("Hosted/Program.cs", facts.AsArray().Single(fact => fact!["id"]!.GetValue<string>() == id)!["locations"]![0]!["path"]!.GetValue<string>());
                }
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
