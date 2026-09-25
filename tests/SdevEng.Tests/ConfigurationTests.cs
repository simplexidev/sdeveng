namespace SdevEng.Tests;

public class ConfigurationTests
{
    [Fact] public void EnvironmentOverridesDefaultsWithoutPersistingSecrets() { var values = new Dictionary<string, string> { ["JEV_MODE"] = "off", ["JEV_TIMEOUT_SECONDS"] = "7", ["JEV_MODEL"] = "test-model" }; var settings = Settings.Load(AgentTool.FindToolkit(), name => values.GetValueOrDefault(name)); Assert.Equal("off", settings.Jev.Mode); Assert.Equal(7, settings.Jev.TimeoutSeconds); Assert.Equal("test-model", settings.Jev.Model); }
    [Fact] public void BadTimeoutFailsExplicitly() => Assert.Throws<ArgumentException>(() => Settings.Load(AgentTool.FindToolkit(), name => name == "JEV_TIMEOUT_SECONDS" ? "bad" : null));
    [Fact] public void RejectsInsecureOrCredentialEndpoint() { Assert.Throws<ArgumentException>(() => new JevSettings { ApiUrl = "http://example.invalid" }.Validate()); Assert.Throws<ArgumentException>(() => new JevSettings { ApiUrl = "https://user:password@example.invalid" }.Validate()); }
}
