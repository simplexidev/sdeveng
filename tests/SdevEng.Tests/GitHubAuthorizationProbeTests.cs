using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class GitHubAuthorizationProbeTests
{
    [Theory]
    [InlineData("https://github.com/acme/widget.git", "acme/widget")]
    [InlineData("git@github.com:acme/widget.git", "acme/widget")]
    public void ParsesCanonicalOrigin(string origin, string expected)
    {
        var target = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(origin);
        Assert.Equal(expected, $"{target.Owner}/{target.Repository}");
    }

    [Fact]
    public async Task ReportsIssueReadAllowedAndLabelWriteUnknownWithoutLeakingSecrets()
    {
        const string secret = "gho_super_secret_token";
        var process = new FakeProcess(
            new(0, "https://github.com/acme/widget.git\n"), new(0, "Logged in to github.com account test\nToken scopes: read:org"),
            new(0, "[]"), new(0, "{\"permissions\":{\"push\":true}}"), new(0, "abcdef123456"), new(1, "network timeout"), new(0, "[]"));
        var result = await new AgentTool.GitHubAuthorizationProbe(process).ProbeAsync("/repo");
        Assert.Equal("allowed", result.Capabilities[0].State);
        Assert.Equal("unknown", result.Capabilities[1].State);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(new[] { "issues.read", "issues.labels.write", "branch-push", "pr-create", "workflow-rerun" }, result.Capabilities.Select(capability => capability.Operation));
        Assert.Equal("unknown", result.Capabilities[2].State);
        Assert.Equal("denied", result.Capabilities[3].State);
        Assert.Equal("unknown", result.Capabilities[4].State);
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Any(argument => argument.Contains("POST", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AllowsLabelWriteOnlyWhenRepositoryAndCredentialEvidenceAgree()
    {
        var process = new FakeProcess(new ProcessResult(0, "https://github.com/acme/widget.git"),
            new ProcessResult(0, "Token scopes: repo"), new ProcessResult(0, "[]"),
            new ProcessResult(0, "{\"private\":true,\"permissions\":{\"push\":true}}"), new ProcessResult(0, "abcdef123456"), new ProcessResult(0, ""), new ProcessResult(0, "[]"));
        var result = await new AgentTool.GitHubAuthorizationProbe(process).ProbeAsync("/repo");
        Assert.Equal("allowed", result.Capabilities[1].State);
        Assert.Equal("allowed", result.Capabilities[3].State);
        Assert.Equal("unknown", result.Capabilities[4].State);
        Assert.Contains(process.Calls, call => call.Executable == "git" && call.Arguments.SequenceEqual(new[] { "push", "--dry-run", "--porcelain", "origin", "HEAD:refs/heads/roadmap/sdeveng-capability-probe-abcdef123456" }));
    }

    [Fact]
    public async Task ReportsExplicitIssueReadDenialAndUnknownForUnparseableRemote()
    {
        var denied = new FakeProcess(new ProcessResult(0, "git@github.com:acme/widget.git"), new ProcessResult(0, "logged in\nToken scopes: repo"), new ProcessResult(1, "HTTP 403 Forbidden"), new ProcessResult(1, "HTTP 403"), new ProcessResult(0, "abcdef123456"), new ProcessResult(1, "remote: error: protected branch hook declined"), new ProcessResult(1, "HTTP 403 Forbidden"));
        var result = await new AgentTool.GitHubAuthorizationProbe(denied).ProbeAsync("/repo");
        Assert.Equal("denied", result.Capabilities[0].State);
        Assert.Equal("denied", result.Capabilities[2].State);
        Assert.Equal("denied", result.Capabilities[4].State);
        var badOrigin = new FakeProcess(new ProcessResult(0, "https://example.com/acme/widget.git"));
        Assert.All((await new AgentTool.GitHubAuthorizationProbe(badOrigin).ProbeAsync("/repo")).Capabilities, capability => Assert.Equal("unknown", capability.State));
        Assert.Single(badOrigin.Calls);
    }

    [Fact]
    public void GithubCapabilitiesRoutesThroughGitHubCommandModule()
    {
        var module = new AgentTool.GitHubCommandModule(authorizationProbe: new AgentTool.GitHubAuthorizationProbe(new FakeProcess(new ProcessResult(0, ""))));
        var cli = Cli.Parse(["github", "capabilities"]);
        Assert.True(module.CanHandle(cli));
    }

    [Fact]
    public async Task GithubCapabilitiesRunsThroughRegisteredRuntimeWithFakeProcess()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        services.AddSingleton<AgentTool.IGitHubAuthorizationProcess>(new FakeProcess(
            new ProcessResult(0, "https://github.com/acme/widget.git"), new ProcessResult(0, "logged in"),
            new ProcessResult(0, "[]"), new ProcessResult(0, "{}"), new ProcessResult(0, "abcdef123456"), new ProcessResult(0, ""), new ProcessResult(0, "[]")));
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["github", "capabilities"]), AgentTool.FindToolkit(), "/repo", new(new(), new(), new(), new()));
        Assert.Equal("ok", result.Status);
        Assert.Equal("allowed", Assert.IsType<AgentTool.GitHubCapabilities>(result.Data).Capabilities[0].State);
        Assert.Equal(new[] { "issues.read", "issues.labels.write", "branch-push", "pr-create", "workflow-rerun" }, Assert.IsType<AgentTool.GitHubCapabilities>(result.Data).Capabilities.Select(capability => capability.Operation));
    }

    [Fact]
    public async Task DryRunProbeDoesNotCreateOrModifyBareRemoteRef()
    {
        using var repo = new TemporaryGitRepository();
        var bare = Path.Combine(Path.GetTempPath(), "capability-probe-bare-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Processes.Run("git", ["init", "--bare", bare], repo.Root);
            repo.Run("remote", "add", "origin", bare);
            var before = (await Processes.Run("git", ["--git-dir", bare, "show-ref", "--heads"], repo.Root)).Output;
            var result = await new AgentTool.GitHubAuthorizationProbe(new BareRemoteProcess()).ProbeAsync(repo.Root);
            Assert.Equal("allowed", result.Capabilities.Single(item => item.Operation == "branch-push").State);
            var after = (await Processes.Run("git", ["--git-dir", bare, "show-ref", "--heads"], repo.Root)).Output;
            Assert.Equal(before, after);
            Assert.DoesNotContain("sdeveng-capability-probe", after);
        }
        finally
        {
            if (Directory.Exists(bare)) Directory.Delete(bare, true);
        }
    }

    private sealed class BareRemoteProcess : AgentTool.IGitHubAuthorizationProcess
    {
        public async Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd)
        {
            var args = arguments.ToArray();
            if (executable == "git" && args.SequenceEqual(new[] { "remote", "get-url", "--all", "origin" }))
                return new(0, "https://github.com/acme/widget.git");
            if (executable == "git") return await Processes.Run(executable, args, cwd);
            if (args.SequenceEqual(new[] { "auth", "status" })) return new(0, "Token scopes: repo");
            if (args.SequenceEqual(new[] { "api", "--method", "GET", "repos/acme/widget/issues?state=all&per_page=1" })) return new(0, "[]");
            if (args.SequenceEqual(new[] { "api", "--method", "GET", "repos/acme/widget" })) return new(0, "{\"permissions\":{\"push\":true}}");
            if (args.SequenceEqual(new[] { "api", "--method", "GET", "repos/acme/widget/actions/runs?per_page=1" })) return new(0, "[]");
            throw new InvalidOperationException("Unexpected authorization probe process invocation.");
        }
    }

    private sealed class FakeProcess(params ProcessResult[] results) : AgentTool.IGitHubAuthorizationProcess
    {
        private readonly Queue<ProcessResult> _results = new(results);
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd)
        {
            var args = arguments.ToArray();
            Calls.Add((executable, args));
            return Task.FromResult(_results.Dequeue());
        }
    }
}
