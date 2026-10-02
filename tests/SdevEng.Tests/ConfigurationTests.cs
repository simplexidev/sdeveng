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
        Assert.Equal("invocation --set", node["precedence"]![5]!.GetValue<string>());
        Assert.NotEmpty(node["sources"]!["files"]!.AsArray());
    }

    [Fact]
    public void MachineUserAndRepositoryLayersApplyInOrder()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "sdeveng-config-" + Guid.NewGuid().ToString("N"));
        var machine = Path.Combine(scratch, "machine");
        var user = Path.Combine(scratch, "user");
        var repo = Path.Combine(scratch, "repo");
        try
        {
            Directory.CreateDirectory(machine);
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(Path.Combine(repo, ".sdeveng"));
            File.WriteAllText(Path.Combine(machine, "output-limits.json"), "{\"maxLines\":20}");
            File.WriteAllText(Path.Combine(user, "output-limits.json"), "{\"maxLines\":30}");
            File.WriteAllText(Path.Combine(repo, ".sdeveng", "output-limits.json"), "{\"maxLines\":40}");
            Assert.Equal(40, Settings.Load(AgentTool.FindToolkit(), _ => null, repo, machine, user).Output.MaxLines);
            File.Delete(Path.Combine(repo, ".sdeveng", "output-limits.json"));
            Assert.Equal(30, Settings.Load(AgentTool.FindToolkit(), _ => null, repo, machine, user).Output.MaxLines);
            File.Delete(Path.Combine(user, "output-limits.json"));
            Assert.Equal(20, Settings.Load(AgentTool.FindToolkit(), _ => null, repo, machine, user).Output.MaxLines);
        }
        finally { Directory.Delete(scratch, true); }
    }

    [Fact]
    public void InvalidRepositoryLayerIdentifiesItsSource()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "sdeveng-config-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(scratch, ".sdeveng");
        try
        {
            Directory.CreateDirectory(config);
            var path = Path.Combine(config, "jev.json");
            File.WriteAllText(path, "{bad");
            var error = Assert.Throws<ArgumentException>(() => Settings.Load(
                AgentTool.FindToolkit(), _ => null, scratch,
                Path.Combine(scratch, "machine"), Path.Combine(scratch, "user")));
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(scratch, true); }
    }

    [Fact]
    public void InvalidLowerPrecedenceLayerFailsEvenWhenOverridden()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "sdeveng-config-" + Guid.NewGuid().ToString("N"));
        var machine = Path.Combine(scratch, "machine");
        var repository = Path.Combine(scratch, "repo");
        try
        {
            Directory.CreateDirectory(machine);
            Directory.CreateDirectory(Path.Combine(repository, ".sdeveng"));
            var invalid = Path.Combine(machine, "output-limits.json");
            File.WriteAllText(invalid, "{\"maxLines\":0}");
            File.WriteAllText(Path.Combine(repository, ".sdeveng", "output-limits.json"), "{\"maxLines\":40}");
            var error = Assert.Throws<ArgumentException>(() => Settings.Load(AgentTool.FindToolkit(), _ => null,
                repository, machine, Path.Combine(scratch, "user")));
            Assert.Contains(invalid, error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(scratch, true); }
    }
}
