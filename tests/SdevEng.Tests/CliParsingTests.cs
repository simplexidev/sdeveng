namespace SdevEng.Tests;

public class CliParsingTests
{
    [Fact] public void PreservesSpacesAndOptionValues() { var c = Cli.Parse(["repo", "locate", "--query", "file name", "--root=/tmp/a b", "--json"]); Assert.Equal("file name", c.Require("query")); Assert.Equal("/tmp/a b", c.Require("root")); Assert.True(c.Flag("json")); Assert.Equal(2, c.Words.Count); }
    [Theory][InlineData("--unknown")][InlineData("--root")][InlineData("--json=true")] public void RejectsInvalid(string arg) => Assert.Throws<ArgumentException>(() => Cli.Parse([arg]));
    [Fact] public void RejectsDuplicate() => Assert.Throws<ArgumentException>(() => Cli.Parse(["--root", "a", "--root", "b"]));
    [Fact] public void RejectsInvalidIssue() => Assert.Throws<ArgumentException>(() => Cli.Parse(["--issue", "-1"]).PositiveInt("issue"));
}
