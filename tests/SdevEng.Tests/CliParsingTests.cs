namespace SdevEng.Tests;

public class CliParsingTests
{
    [Fact] public void PreservesSpacesAndOptionValues() { var c = Cli.Parse(["repo", "locate", "--query", "file name", "--root=/tmp/a b", "--json"]); Assert.Equal("file name", c.Require("query")); Assert.Equal("/tmp/a b", c.Require("root")); Assert.True(c.Flag("json")); Assert.Equal(2, c.Words.Count); }
    [Theory][InlineData("--unknown")][InlineData("--root")][InlineData("--json=true")] public void RejectsInvalid(string arg) => Assert.Throws<ArgumentException>(() => Cli.Parse([arg]));
    [Fact] public void RejectsDuplicate() => Assert.Throws<ArgumentException>(() => Cli.Parse(["--root", "a", "--root", "b"]));
    [Fact] public void ParsesRepeatableConfigurationOverrides() { var c = Cli.Parse(["repo", "summary", "--set", "JEV_MODE=off", "--set=JEV_MODEL=model=variant"]); Assert.Equal(new[] { new KeyValuePair<string, string?>("JEV_MODE", "off"), new KeyValuePair<string, string?>("JEV_MODEL", "model=variant") }, c.ConfigurationOverrides()); }
    [Theory][InlineData("UNKNOWN=value")][InlineData("JEV_MODE=")][InlineData("broken")] public void RejectsUnsupportedConfigurationOverride(string value) => Assert.Throws<ArgumentException>(() => Cli.Parse(["repo", "summary", "--set", value]).ConfigurationOverrides().ToArray());
    [Fact] public void RejectsInvalidIssue() => Assert.Throws<ArgumentException>(() => Cli.Parse(["--issue", "-1"]).PositiveInt("issue"));
    [Fact] public void SupportsStableVersionSwitch() { var c = Cli.Parse(["-V", "--json"]); Assert.True(c.Flag("version")); Assert.True(c.Flag("json")); }
    [Fact] public void JsonEnvelopeCarriesStableIdentityAndCommand() { var node = System.Text.Json.Nodes.JsonNode.Parse(AgentTool.SerializeEnvelope(Result.Ok(new { kind = "test" }), "repo summary", null))!; Assert.Equal(1, node["schemaVersion"]!.GetValue<int>()); Assert.Equal("sdeveng", node["product"]!.GetValue<string>()); Assert.Equal("3.0.0", node["cliVersion"]!.GetValue<string>()); Assert.Equal("repo summary", node["command"]!.GetValue<string>()); Assert.Equal(0, node["exitCode"]!.GetValue<int>()); Assert.Null(node["error"]); }
    [Fact] public void JsonOutputLimitCountsUtf8Bytes() { using var repo = new TemporaryGitRepository(); var output = AgentTool.RenderJson(Result.Ok(new { text = string.Concat(Enumerable.Repeat("界", 500)) }), "repo summary", repo.Root, new() { MaxOutputChars = 1024 }); Assert.True(System.Text.Encoding.UTF8.GetByteCount(output) <= 1024); var node = System.Text.Json.Nodes.JsonNode.Parse(output)!; Assert.True(node["data"]!["truncated"]!.GetValue<bool>()); }
}
