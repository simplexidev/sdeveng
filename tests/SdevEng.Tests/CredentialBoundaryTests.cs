using System.Text.Json;

namespace SdevEng.Tests;

public class CredentialBoundaryTests
{
    static string FakeKey() => string.Join('-', new[] { "phase2", "synthetic", "boundary", "9f13" });

    [Fact]
    public void StatusReportsOnlyConfiguredOrUnavailable()
    {
        Assert.Equal("JEV credentials: unavailable", JevCredentials.Status(_ => null));
        Assert.Equal("JEV credentials: configured", JevCredentials.Status(_ => FakeKey()));
    }

    [Fact]
    public void RedactionRemovesTheWholeCredential()
    {
        var key = FakeKey();
        WithCredential(key, () =>
        {
            var redacted = Secrets.Redact($"before {key} after");
            Assert.Equal("before [REDACTED] after", redacted);
            Assert.DoesNotContain(key[..8], redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(key[^8..], redacted, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void StructuredAndOverflowOutputRedactEscapedCredentials()
    {
        var key = "phase2-\"synthetic\\boundary";
        WithCredential(key, () =>
        {
            using var repo = new TemporaryGitRepository();
            var output = AgentTool.Render(Result.Ok(new { id = key, values = new[] { "safe", key } }), repo.Root, new() { MaxOutputChars = 20 });
            Assert.DoesNotContain(key, output, StringComparison.Ordinal);
            Assert.True(JsonDocument.Parse(output).RootElement.GetProperty("truncated").GetBoolean());
            var artifact = Directory.GetFiles(Path.Combine(repo.Root, ".agent-tool"), "*.json").Single();
            using var parsed = JsonDocument.Parse(File.ReadAllText(artifact));
            Assert.Equal("[REDACTED]", parsed.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.Equal("[REDACTED]", parsed.RootElement.GetProperty("data").GetProperty("values")[1].GetString());
        });
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("off", false)]
    [InlineData("required", false)]
    public async Task SensitiveScreenIdIsRefusedBeforeAnyCommandPath(string mode, bool dryRun)
    {
        var key = FakeKey();
        await WithCredential(key, async () =>
        {
            using var repo = new TemporaryGitRepository(); var input = Path.Combine(repo.Root, "screen.json");
            File.WriteAllText(input, $"{{\"capability\":\"relevance\",\"purpose\":\"candidate-relevance\",\"deterministicNarrowed\":true,\"query\":\"relevant?\",\"candidates\":[{{\"id\":\"{key}\",\"text\":\"safe\"}}]}}");
            var args = new List<string> { "jev", "screen", "--input", input };
            if (dryRun) args.Add("--dry-run"); else args.Add("--safe-input");
            var loaded = Settings.Load(AgentTool.FindToolkit(), _ => null);
            var result = await AgentTool.Execute(Cli.Parse([.. args]), AgentTool.FindToolkit(), repo.Root, loaded with { Jev = loaded.Jev with { Mode = mode } });
            var output = AgentTool.Render(result, repo.Root, new());
            Assert.Equal("REVIEW", result.Status); Assert.DoesNotContain(key, output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agent-tool", "jev-cache")));
        });
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("git")]
    [InlineData("gh")]
    [InlineData("formatter-or-analyzer")]
    public void ChildProcessEnvironmentStripsCredential(string executable)
    {
        var key = FakeKey();
        WithCredential(key, () =>
        {
            var info = Processes.StartInfo(executable, ["--version"], AgentTool.FindToolkit());
            Assert.False(info.Environment.ContainsKey(JevCredentials.EnvironmentVariable));
            Assert.DoesNotContain(info.ArgumentList, argument => argument.Contains(key, StringComparison.Ordinal));
            Assert.Throws<InvalidOperationException>(() => Processes.StartInfo(executable, ["--version=" + key], AgentTool.FindToolkit()));
        });
    }

    [Fact]
    public async Task AgentToolJevRequestReadsCredentialAtTheHttpBoundary()
    {
        var key = FakeKey();
        await WithCredential(key, async () =>
        {
            using var repo = new TemporaryGitRepository();
            using var handler = new FakeHttpMessageHandler(JevClientTests.Good);
            using var http = new HttpClient(handler);
            var result = await new JevClient(http, new(), Path.Combine(repo.Root, "cache")).Judge(JevClientTests.Request());
            Assert.Equal("INCLUDE", result.Status);
            Assert.Equal("Bearer", handler.Authorization);
            Assert.Equal(key, handler.AuthorizationParameter);
        });
    }

    [Fact]
    public async Task InvalidCredentialIsReportedWithoutDisclosure()
    {
        var key = FakeKey() + "\r\ninvalid";
        await WithCredential(key, async () =>
        {
            using var handler = new FakeHttpMessageHandler(JevClientTests.Good);
            using var http = new HttpClient(handler);
            var result = await new JevClient(http, new(), "/unused").Judge(JevClientTests.Request());
            var output = JsonSerializer.Serialize(result, AgentTool.Json);
            Assert.Equal("REVIEW", result.Status);
            Assert.Equal(0, handler.Calls);
            Assert.DoesNotContain(key, output, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeKey(), output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CredentialIsNotPersistedToCacheConfigurationOrLogs()
    {
        var key = FakeKey();
        await WithCredential(key, async () =>
        {
            using var repo = new TemporaryGitRepository();
            var response = JevClientTests.Good[..^1] + ",\"ignoredEcho\":\"" + key + "\"}";
            using var handler = new FakeHttpMessageHandler(response);
            using var http = new HttpClient(handler);
            await new JevClient(http, new(), Path.Combine(repo.Root, "cache")).Judge(JevClientTests.Request());
            _ = Settings.Load(AgentTool.FindToolkit());
            _ = await AgentTool.RunArtifact("dotnet", ["--version"], repo.Root, Path.Combine(repo.Root, "logs"), new());

            var files = new[] { Path.Combine(repo.Root, "cache"), Path.Combine(repo.Root, "logs") }
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateFiles(Path.Combine(AgentTool.FindToolkit(), "config"), "*", SearchOption.AllDirectories));
            Assert.All(files, path => Assert.DoesNotContain(key, File.ReadAllText(path), StringComparison.Ordinal));
        });
    }

    [Fact]
    public void CliHasNoApiKeyOption()
    {
        Assert.Throws<ArgumentException>(() => Cli.Parse(["jev", "noul", "--api-key", FakeKey()]));
    }

    [Fact]
    public async Task DoctorReportsStatusWithoutCredentialDetails()
    {
        var key = FakeKey();
        await WithCredential(key, async () =>
        {
            using var repo = new TemporaryGitRepository();
            var toolkit = AgentTool.FindToolkit();
            var result = await AgentTool.Execute(Cli.Parse(["doctor", "--home", repo.Root]), toolkit, repo.Root, Settings.Load(toolkit));
            var output = JsonSerializer.Serialize(result, AgentTool.Json);
            Assert.Contains("JEV credentials: configured", output, StringComparison.Ordinal);
            Assert.DoesNotContain(key, output, StringComparison.Ordinal);
            Assert.DoesNotContain(key[..8], output, StringComparison.Ordinal);
            Assert.DoesNotContain(key[^8..], output, StringComparison.Ordinal);
        });
    }

    static void WithCredential(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(JevCredentials.EnvironmentVariable);
        try { Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, value); action(); }
        finally { Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, previous); }
    }

    static async Task WithCredential(string? value, Func<Task> action)
    {
        var previous = Environment.GetEnvironmentVariable(JevCredentials.EnvironmentVariable);
        try { Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, value); await action(); }
        finally { Environment.SetEnvironmentVariable(JevCredentials.EnvironmentVariable, previous); }
    }
}
