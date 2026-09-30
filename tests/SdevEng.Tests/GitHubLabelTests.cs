namespace SdevEng.Tests;

public sealed class GitHubLabelTests
{
    [Fact]
    public async Task ListsConfiguredAndRemoteLabelsAndCreatesMissingOnlyWithApply()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var process = new FakeLabelProcess();
        var module = new AgentTool.GitHubCommandModule(labelProcess: process);

        var listed = await module.Execute(Cli.Parse(["github", "labels"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        var list = System.Text.Json.JsonSerializer.SerializeToElement(listed.Data, AgentTool.Json);
        Assert.Equal("github-labels", list.GetProperty("kind").GetString());
        Assert.Equal(13, list.GetProperty("configured").GetArrayLength());
        Assert.DoesNotContain(process.Calls, call => call.Contains("POST", StringComparer.Ordinal));
        Assert.Equal(13, list.GetProperty("missing").GetArrayLength());

        process.Calls.Clear();
        var applied = await module.Execute(Cli.Parse(["github", "labels", "--apply"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        var result = System.Text.Json.JsonSerializer.SerializeToElement(applied.Data, AgentTool.Json);
        Assert.Equal(13, result.GetProperty("created").GetArrayLength());
        Assert.DoesNotContain(result.GetProperty("missing").EnumerateArray(), _ => true);
        Assert.Equal(13, process.Calls.Count(call => call.Contains("POST", StringComparer.Ordinal)));
        Assert.All(process.Calls.Where(call => call.Contains("POST", StringComparer.Ordinal)), call =>
        {
            Assert.Contains(call, argument => argument.StartsWith("color=", StringComparison.Ordinal));
            Assert.Contains(call, argument => argument.StartsWith("description=", StringComparison.Ordinal));
        });
    }

    private sealed class FakeLabelProcess : IGitHubLabelProcess
    {
        public List<string[]> Calls { get; } = [];
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            return Task.FromResult(args.Contains("POST", StringComparer.Ordinal)
                ? new ProcessResult(0, "{}")
                : new ProcessResult(0, "[]"));
        }
    }
}
