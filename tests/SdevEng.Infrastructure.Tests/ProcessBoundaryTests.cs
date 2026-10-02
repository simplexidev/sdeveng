using SdevEng;

public class ProcessBoundaryTests
{
    [Fact]
    public void ChildProcessDropsJevCredential()
    {
        var key = Environment.GetEnvironmentVariable(JevCredentials.EnvironmentVariable);
        Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, "synthetic-test-credential");
        try
        {
            var info = Processes.StartInfo("dotnet", ["--version"], Environment.CurrentDirectory);
            Assert.False(info.Environment.ContainsKey(JevCredentials.EnvironmentVariable));
            Assert.Throws<InvalidOperationException>(() => Processes.StartInfo("dotnet",
                ["synthetic-test-credential"], Environment.CurrentDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, key);
        }
    }
}
