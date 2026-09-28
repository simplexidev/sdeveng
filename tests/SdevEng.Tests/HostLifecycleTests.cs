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

    private sealed class TestCommandModule : AgentTool.ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "feature sample";
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Ok("feature"));
    }
}
