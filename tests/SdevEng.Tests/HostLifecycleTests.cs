using Microsoft.Extensions.Logging.Abstractions;

namespace SdevEng.Tests;

public sealed class HostLifecycleTests
{
    [Fact]
    public async Task RuntimeDoesNotStartACommandAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runtime = new AgentTool.AgentToolRuntime(NullLogger<AgentTool.AgentToolRuntime>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.Execute(
            Cli.Parse(["version"]), "", "", new Settings(new(), new(), new(), new()), cancellation.Token));
    }
}
