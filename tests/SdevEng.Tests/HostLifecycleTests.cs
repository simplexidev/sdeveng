using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

public sealed class HostLifecycleTests
{
    [Fact]
    public async Task RuntimeDoesNotStartACommandAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runtime = new AgentTool.AgentToolRuntime(NullLogger<AgentTool.AgentToolRuntime>.Instance, []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Execute(
            Cli.Parse(["version"]), "", "", new Settings(new(), new(), new(), new()), cancellation.Token));
    }

    [Fact]
    public async Task RuntimeDispatchesToRegisteredCommandModule()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AgentTool.ICommandModule, TestCommandModule>();
        using var provider = services.BuildServiceProvider();
        var runtime = new AgentTool.AgentToolRuntime(
            NullLogger<AgentTool.AgentToolRuntime>.Instance,
            provider.GetServices<AgentTool.ICommandModule>());

        var result = await runtime.Execute(Cli.Parse(["feature sample"]), "", "", new Settings(new(), new(), new(), new()));

        Assert.Equal("feature", result.Data);
    }

    [Fact]
    public async Task InstallerModuleOwnsOnlyInstallerCommandsAndDispatchesDryRun()
    {
        var module = new AgentTool.InstallerCommandModule();
        Assert.True(module.CanHandle(Cli.Parse(["install"])));
        Assert.True(module.CanHandle(Cli.Parse(["update"])));
        Assert.True(module.CanHandle(Cli.Parse(["uninstall"])));
        Assert.False(module.CanHandle(Cli.Parse(["doctor"])));

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<AgentTool.AgentToolRuntime>();
        var home = Path.Combine(Path.GetTempPath(), "sdeveng-installer-module-" + Guid.NewGuid().ToString("N"));
        var command = Cli.Parse(["install", "--home", home, "--dry-run"]);

        var result = await runtime.Execute(command, AgentTool.FindToolkit(), "", new Settings(new(), new(), new(), new()));

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task DoctorModuleOwnsDoctorAndDispatchesChecks()
    {
        var module = new AgentTool.DoctorCommandModule();
        Assert.True(module.CanHandle(Cli.Parse(["doctor"])));
        Assert.False(module.CanHandle(Cli.Parse(["version"])));

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["doctor", "--home", Path.GetTempPath()]), AgentTool.FindToolkit(), Path.GetTempPath(), new(new(), new(), new(), new()));

        using var data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json));
        Assert.Equal(4, data.RootElement.GetProperty("checks").GetArrayLength());
        Assert.True(data.RootElement.TryGetProperty("installation", out _));
    }

    [Fact]
    public async Task GitModuleOwnsOnlyRequestedGitCommandsAndDispatchesState()
    {
        var module = new AgentTool.GitCommandModule();
        foreach (var command in new[] { "git state", "git summary", "git conflict-forecast", "git prepare-commit", "git issue-start" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["repo", "summary"])));

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["git", "state"]), AgentTool.FindToolkit(), Environment.CurrentDirectory,
            new(new(), new(), new(), new()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task RepoModuleOwnsFiniteRepoFamilyAndDispatchesLocate()
    {
        var module = new AgentTool.RepoCommandModule();
        foreach (var command in new[] { "repo changed-files", "repo summary", "repo locate", "repo affected-projects", "repo ownership", "repo health", "repo hygiene" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["git", "state"])));

        using var repo = new TemporaryGitRepository();
        repo.Write("docs/target.md", "target");
        var result = await module.Execute(
            Cli.Parse(["repo", "locate", "--query", "target"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("docs/target.md", System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubModuleOwnsOnlyRequestedCommandsAndDispatchesPreparePr()
    {
        var module = new AgentTool.GitHubCommandModule();
        foreach (var command in new[] { "github prepare-pr", "github pr-status", "github review-comments", "github actions" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["github issue"])));

        using var repo = new TemporaryGitRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["github", "prepare-pr"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Status);
        Assert.Contains("state", System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DotnetModuleOwnsFiniteFamilyAndDispatchesDiagnosticsPlan()
    {
        var module = new AgentTool.DotnetCommandModule();
        var commands = new[]
        {
            "dotnet verify", "dotnet format", "dotnet package-audit", "dotnet dependencies",
            "dotnet api-check", "dotnet release-verify", "dotnet inspect", "dotnet build-plan",
            "dotnet test-plan", "dotnet diagnostics-plan"
        };
        foreach (var command in commands) Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["logs", "summarize"])));

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["dotnet", "diagnostics-plan", "--signal", "cpu", "--duration-seconds", "5"]),
            AgentTool.FindToolkit(), Environment.CurrentDirectory, new(new(), new(), new(), new()));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Status);
        Assert.Contains("dotnet-trace", System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportModuleOwnsAndDispatchesFiniteReportFamily()
    {
        var module = new AgentTool.ReportCommandModule();
        var commands = new[]
        {
            "logs summarize", "sarif summarize", "artifact inspect", "artifact verify",
            "test-results summarize", "coverage summarize"
        };
        foreach (var command in commands) Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["dotnet", "inspect"])));

        var file = Path.Combine(Path.GetTempPath(), "sdeveng-report-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await File.WriteAllTextAsync(file, "first line\nsecond line\n");
            var result = await module.Execute(Cli.Parse(["logs", "summarize", "--file", file]),
                AgentTool.FindToolkit(), "", new(new(), new(), new(), new()), CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("ok", result.Status);
            Assert.Contains("first line", System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class TestCommandModule : AgentTool.ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "feature sample";
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Ok("feature"));
    }
}
