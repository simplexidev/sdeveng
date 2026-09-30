namespace SdevEng.Tests;

public sealed class GitHubLabelTests
{
    [Fact]
    public async Task ListsConfiguredAndRemoteLabelsAndCreatesMissingOnlyWithApply()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var process = new FakeLabelProcess();
        process.RemoteLabels = "[[{\"name\":\"custom:keep\"}]]";
        var module = new AgentTool.GitHubCommandModule(labelProcess: process);

        var listed = await module.Execute(Cli.Parse(["github", "labels"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        var list = System.Text.Json.JsonSerializer.SerializeToElement(listed.Data, AgentTool.Json);
        Assert.Equal("github-labels", list.GetProperty("kind").GetString());
        Assert.True(list.GetProperty("dryRun").GetBoolean());
        Assert.Equal(14, list.GetProperty("configured").GetArrayLength());
        Assert.DoesNotContain(process.Calls, call => call.Contains("POST", StringComparer.Ordinal));
        Assert.Equal(14, list.GetProperty("missing").GetArrayLength());
        Assert.Equal(new[] { "custom:keep" }, list.GetProperty("unmanaged").EnumerateArray().Select(label => label.GetString()));
        Assert.DoesNotContain(process.Calls, call => call.Contains("DELETE", StringComparer.Ordinal));

        process.Calls.Clear();
        var dryRun = await module.Execute(Cli.Parse(["github", "labels", "--dry-run"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        var dryRunResult = System.Text.Json.JsonSerializer.SerializeToElement(dryRun.Data, AgentTool.Json);
        Assert.True(dryRunResult.GetProperty("dryRun").GetBoolean());
        Assert.Equal(14, dryRunResult.GetProperty("missing").GetArrayLength());
        Assert.Empty(dryRunResult.GetProperty("created").EnumerateArray());
        Assert.DoesNotContain(process.Calls, call => call.Contains("POST", StringComparer.Ordinal));

        process.Calls.Clear();
        var applied = await module.Execute(Cli.Parse(["github", "labels", "--apply"]), AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()), CancellationToken.None);
        var result = System.Text.Json.JsonSerializer.SerializeToElement(applied.Data, AgentTool.Json);
        Assert.Equal(14, result.GetProperty("created").GetArrayLength());
        Assert.Equal(new[] { "custom:keep" }, result.GetProperty("unmanaged").EnumerateArray().Select(label => label.GetString()));
        Assert.DoesNotContain(result.GetProperty("missing").EnumerateArray(), _ => true);
        Assert.Equal(14, process.Calls.Count(call => call.Contains("POST", StringComparer.Ordinal)));
        Assert.DoesNotContain(process.Calls, call => call.Contains("DELETE", StringComparer.Ordinal));
        Assert.All(process.Calls.Where(call => call.Contains("POST", StringComparer.Ordinal)), call =>
        {
            Assert.Contains(call, argument => argument.StartsWith("color=", StringComparison.Ordinal));
            Assert.Contains(call, argument => argument.StartsWith("description=", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void RejectsConflictingApplyAndDryRunOptions()
    {
        var command = Cli.Parse(["github", "labels", "--dry-run", "--apply"]);
        Assert.Throws<ArgumentException>(() => command.ValidateCommand(command.Command));
    }

    private sealed class FakeLabelProcess : IGitHubLabelProcess
    {
        public List<string[]> Calls { get; } = [];
        public string RemoteLabels { get; set; } = "[]";
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd)
        {
            var args = arguments.ToArray();
            Calls.Add(args);
            return Task.FromResult(args.Contains("POST", StringComparer.Ordinal)
                ? new ProcessResult(0, "{}")
                : new ProcessResult(0, RemoteLabels));
        }
    }
}
