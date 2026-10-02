namespace SdevEng.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void CliDoesNotIssueProcessLevelGitCommandsOrLinkLauncherSource()
    {
        var root = AgentTool.FindToolkit();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "SdevEng.Cli"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Git.Require(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Processes.Run(\"git\"", source, StringComparison.Ordinal);
        }
        var project = File.ReadAllText(Path.Combine(root, "src", "SdevEng.Cli", "SdevEng.Cli.csproj"));
        Assert.DoesNotContain("tools/AgentTool.cs", project, StringComparison.Ordinal);
    }
}
