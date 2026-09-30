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
            new(0, "[]"), new(0, "{\"permissions\":{\"push\":true}}"));
        var result = await new AgentTool.GitHubAuthorizationProbe(process).ProbeAsync("/repo");
        Assert.Equal("allowed", result.Capabilities[0].State);
        Assert.Equal("unknown", result.Capabilities[1].State);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(new[] { "issues.read", "issues.labels.write" }, result.Capabilities.Select(capability => capability.Operation));
        Assert.DoesNotContain(process.Calls, call => call.Arguments.Any(argument => argument.Contains("POST", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task AllowsLabelWriteOnlyWhenRepositoryAndCredentialEvidenceAgree()
    {
        var process = new FakeProcess(new ProcessResult(0, "https://github.com/acme/widget.git"),
            new ProcessResult(0, "Token scopes: repo"), new ProcessResult(0, "[]"),
            new ProcessResult(0, "{\"private\":true,\"permissions\":{\"push\":true}}"));
        var result = await new AgentTool.GitHubAuthorizationProbe(process).ProbeAsync("/repo");
        Assert.Equal("allowed", result.Capabilities[1].State);
    }

    [Fact]
    public async Task ReportsExplicitIssueReadDenialAndUnknownForUnparseableRemote()
    {
        var denied = new FakeProcess(new ProcessResult(0, "git@github.com:acme/widget.git"), new ProcessResult(0, "logged in"), new ProcessResult(1, "HTTP 403 Forbidden"), new ProcessResult(1, "HTTP 403"));
        var result = await new AgentTool.GitHubAuthorizationProbe(denied).ProbeAsync("/repo");
        Assert.Equal("denied", result.Capabilities[0].State);
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
            new ProcessResult(0, "[]"), new ProcessResult(0, "{}")));
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["github", "capabilities"]), AgentTool.FindToolkit(), "/repo", new(new(), new(), new(), new()));
        Assert.Equal("ok", result.Status);
        Assert.Equal("allowed", Assert.IsType<AgentTool.GitHubCapabilities>(result.Data).Capabilities[0].State);
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
