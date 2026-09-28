using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace SdevEng.Tests;

internal static class CommandTestRuntime
{
    private static readonly IServiceProvider Services = CreateServices();

    public static Task<Result> Execute(Cli command, string toolkit, string root, Settings settings) =>
        Services.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(command, toolkit, root, settings);

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        return services.BuildServiceProvider();
    }
}
