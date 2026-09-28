namespace SdevEng.Tests;

public class ConfigurationTests
{
    [Fact]
    public async Task HostValidatesTypedSettingsAtStartup()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        Settings.AddConfigurationSources(builder.Configuration, AgentTool.FindToolkit());
        builder.Configuration["JEV_MODE"] = "invalid";
        Settings.RegisterOptions(builder.Services, builder.Configuration, AgentTool.FindToolkit());
        using var host = builder.Build();
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void EnvironmentOverridesDefaultsWithoutPersistingSecrets() { var values = new Dictionary<string, string> { ["JEV_MODE"] = "off", ["JEV_TIMEOUT_SECONDS"] = "7", ["JEV_MODEL"] = "test-model" }; var settings = Settings.Load(AgentTool.FindToolkit(), name => values.GetValueOrDefault(name)); Assert.Equal("off", settings.Jev.Mode); Assert.Equal(7, settings.Jev.TimeoutSeconds); Assert.Equal("test-model", settings.Jev.Model); }
    [Fact]
    public void CliConfigurationOverrideTakesPrecedenceOverEnvironment()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        Settings.AddConfigurationSources(builder.Configuration, AgentTool.FindToolkit());
        builder.Configuration["JEV_MODE"] = "required";
        foreach (var (name, value) in Cli.Parse(["repo", "summary", "--set", "JEV_MODE=off"]).ConfigurationOverrides()) builder.Configuration[name] = value;
        Assert.Equal("off", Settings.Load(AgentTool.FindToolkit(), name => builder.Configuration[name]).Jev.Mode);
    }
    [Fact]
    public void BadTimeoutFailsExplicitly() => Assert.Throws<ArgumentException>(() => Settings.Load(AgentTool.FindToolkit(), name => name == "JEV_TIMEOUT_SECONDS" ? "bad" : null));
    [Fact]
    public void RejectsInsecureOrCredentialEndpoint() { Assert.Throws<ArgumentException>(() => new JevSettings { ApiUrl = "http://example.invalid" }.Validate()); Assert.Throws<ArgumentException>(() => new JevSettings { ApiUrl = "https://user:password@example.invalid" }.Validate()); }

    [Fact]
    public async Task ExplainReportsEffectiveSettingsAndConfigurationPrecedence()
    {
        var configured = Settings.Load(AgentTool.FindToolkit(), name => name == "JEV_MODE" ? "off" : null);
        var result = await CommandTestRuntime.Execute(Cli.Parse(["config", "explain"]), AgentTool.FindToolkit(), Environment.CurrentDirectory, configured);
        var node = System.Text.Json.JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        Assert.Equal("effective-config", node["kind"]!.GetValue<string>());
        Assert.Equal("off", node["settings"]!["jev"]!["mode"]!.GetValue<string>());
        Assert.Equal("invocation --set overrides", node["precedence"]![2]!.GetValue<string>());
        Assert.Contains("No separate machine- or user-level configuration files", node["sources"]!["machineAndUserFiles"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
