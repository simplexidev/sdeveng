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

    private sealed class TestCommandModule : AgentTool.ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "feature sample";
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Ok("feature"));
    }
}
