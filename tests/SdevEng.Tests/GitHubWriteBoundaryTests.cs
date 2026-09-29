using System.Text.RegularExpressions;

public sealed class GitHubWriteBoundaryTests
{
    [Fact]
    public void ProductionHasNoGitHubWriteConsumerYet()
    {
        var source = File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tools/AgentTool.cs"));

        Assert.Single(Regex.Matches(source, @"\bIGitHubWriteClient\b").Cast<Match>());
        Assert.Contains("Processes.Run(\"gh\", [\"auth\", \"status\"]", source);
        Assert.DoesNotContain("Processes.Run(\"gh\", [\"pr\", \"", source);
    }
}
