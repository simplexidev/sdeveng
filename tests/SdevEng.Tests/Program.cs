namespace SdevEng.Tests;

internal static class Program
{
    public static int Main(string[] args) => SdevEng.AgentTool.Main(args).GetAwaiter().GetResult();
}
