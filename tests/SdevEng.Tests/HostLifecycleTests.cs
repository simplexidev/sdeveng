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
    public async Task RuntimeRejectsCommandsWithoutARegisteredOwner()
    {
        var runtime = new AgentTool.AgentToolRuntime(
            NullLogger<AgentTool.AgentToolRuntime>.Instance,
            []);

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Execute(
            Cli.Parse(["unowned-command"]), "", "", new Settings(new(), new(), new(), new())));
    }

    [Fact]
    public void EveryDocumentedExecutionCommandHasExactlyOneFiniteModuleOwner()
    {
        var documentedCommands = new (string Command, Type Module)[]
        {
            ("install", typeof(AgentTool.InstallerCommandModule)),
            ("update", typeof(AgentTool.InstallerCommandModule)),
            ("uninstall", typeof(AgentTool.InstallerCommandModule)),
            ("doctor", typeof(AgentTool.DoctorCommandModule)),
            ("repo changed-files", typeof(AgentTool.RepoCommandModule)),
            ("repo summary", typeof(AgentTool.RepoCommandModule)),
            ("repo locate", typeof(AgentTool.RepoCommandModule)),
            ("repo health", typeof(AgentTool.RepoCommandModule)),
            ("repo hygiene", typeof(AgentTool.RepoCommandModule)),
            ("repo affected-projects", typeof(AgentTool.RepoCommandModule)),
            ("repo ownership", typeof(AgentTool.RepoCommandModule)),
            ("git state", typeof(AgentTool.GitCommandModule)),
            ("git summary", typeof(AgentTool.GitCommandModule)),
            ("git conflict-forecast", typeof(AgentTool.GitCommandModule)),
            ("git prepare-commit", typeof(AgentTool.GitCommandModule)),
            ("git issue-start", typeof(AgentTool.GitCommandModule)),
            ("git branch-create", typeof(AgentTool.GitCommandModule)),
            ("git worktree-create", typeof(AgentTool.GitCommandModule)),
            ("github pr-status", typeof(AgentTool.GitHubCommandModule)),
            ("github review-comments", typeof(AgentTool.GitHubCommandModule)),
            ("github prepare-pr", typeof(AgentTool.GitHubCommandModule)),
            ("github actions", typeof(AgentTool.GitHubCommandModule)),
            ("dotnet inspect", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet build-plan", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet test-plan", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet diagnostics-plan", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet verify", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet format", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet dependencies", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet package-audit", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet api-check", typeof(AgentTool.DotnetCommandModule)),
            ("dotnet release-verify", typeof(AgentTool.DotnetCommandModule)),
            ("logs summarize", typeof(AgentTool.ReportCommandModule)),
            ("sarif summarize", typeof(AgentTool.ReportCommandModule)),
            ("artifact inspect", typeof(AgentTool.ReportCommandModule)),
            ("artifact verify", typeof(AgentTool.ReportCommandModule)),
            ("test-results summarize", typeof(AgentTool.ReportCommandModule)),
            ("coverage summarize", typeof(AgentTool.ReportCommandModule)),
            ("jev noul", typeof(AgentTool.JevCommandModule)),
            ("jev choice", typeof(AgentTool.JevCommandModule)),
            ("jev score", typeof(AgentTool.JevCommandModule)),
            ("jev screen", typeof(AgentTool.JevCommandModule)),
            ("jev cache-clear", typeof(AgentTool.JevCommandModule)),
            ("upstream status", typeof(AgentTool.UpstreamCommandModule)),
            ("upstream update", typeof(AgentTool.UpstreamCommandModule)),
            ("upstream dotnet-skills", typeof(AgentTool.UpstreamCommandModule)),
            ("validate", typeof(AgentTool.ValidateCommandModule)),
            ("eval", typeof(AgentTool.EvalCommandModule)),
            ("release", typeof(AgentTool.ReleaseCommandModule)),
            ("run status", typeof(AgentTool.RunCommandModule)),
            ("run explain", typeof(AgentTool.RunCommandModule)),
            ("run resume", typeof(AgentTool.RunCommandModule)),
            ("run cancel", typeof(AgentTool.RunCommandModule)),
            ("run list", typeof(AgentTool.RunCommandModule)),
            ("run abandon", typeof(AgentTool.RunCommandModule)),
            ("results init", typeof(AgentTool.ResultsCommandModule)),
            ("results new", typeof(AgentTool.ResultsCommandModule)),
            ("results list", typeof(AgentTool.ResultsCommandModule)),
            ("results latest", typeof(AgentTool.ResultsCommandModule)),
            ("results context", typeof(AgentTool.ResultsCommandModule)),
            ("results clean", typeof(AgentTool.ResultsCommandModule))
        };
        var modules = new AgentTool.ICommandModule[]
        {
            new AgentTool.InstallerCommandModule(), new AgentTool.DoctorCommandModule(),
            new AgentTool.RepoCommandModule(), new AgentTool.GitCommandModule(),
            new AgentTool.GitHubCommandModule(), new AgentTool.DotnetCommandModule(),
            new AgentTool.ReportCommandModule(), new AgentTool.JevCommandModule(),
            new AgentTool.UpstreamCommandModule(), new AgentTool.ValidateCommandModule(),
            new AgentTool.EvalCommandModule(), new AgentTool.ReleaseCommandModule(),
            new AgentTool.RunCommandModule(), new AgentTool.ResultsCommandModule()
        };

        Assert.DoesNotContain(modules, module => module.GetType().Name.Contains("ExistingCommands", StringComparison.Ordinal));
        foreach (var (command, intendedModule) in documentedCommands)
        {
            var parsed = Cli.Parse(command.Split(' '));
            Assert.Equal(intendedModule, Assert.Single(modules, module => module.CanHandle(parsed)).GetType());
        }
    }

    [Fact]
    public async Task RegisteredHostDispatchesAllRunOperations()
    {
        using var repo = new TemporaryGitRepository();
        var runDirectory = Path.Combine(repo.Root, ".sdeveng", "runs");
        var resumable = Guid.NewGuid();
        var abandonable = Guid.NewGuid();
        LocalRunEventStore.AppendTransition(runDirectory, resumable, null, "created");
        LocalRunEventStore.AppendTransition(runDirectory, resumable, "created", "paused");
        LocalRunEventStore.AppendTransition(runDirectory, abandonable, null, "running");

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<AgentTool.AgentToolRuntime>();
        var settings = new Settings(new(), new(), new(), new());
        var toolkit = AgentTool.FindToolkit();
        var operations = new[]
        {
            $"run status {resumable:D}", $"run explain {resumable:D}", $"run resume {resumable:D}",
            $"run cancel {resumable:D}", "run list", $"run abandon {abandonable:D}"
        };

        foreach (var operation in operations)
        {
            var result = await runtime.Execute(Cli.Parse(operation.Split(' ')), toolkit, repo.Root, settings);
            Assert.Equal("ok", result.Status);
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Fact]
    public async Task ValidateModuleOwnsAndDispatchesValidation()
    {
        var module = new AgentTool.ValidateCommandModule();
        Assert.True(module.CanHandle(Cli.Parse(["validate"])));
        Assert.False(module.CanHandle(Cli.Parse(["eval"])));

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var toolkit = AgentTool.FindToolkit();
        var expected = Validation.Run(toolkit);
        var actual = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            Cli.Parse(["validate"]), toolkit, Environment.CurrentDirectory, new(new(), new(), new(), new()));

        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Data, AgentTool.Json),
            System.Text.Json.JsonSerializer.Serialize(actual.Data, AgentTool.Json));
    }

    [Fact]
    public async Task EvalModuleOwnsAndDispatchesEvaluationArguments()
    {
        var module = new AgentTool.EvalCommandModule();
        Assert.True(module.CanHandle(Cli.Parse(["eval", "--skill", "dotnet-test-quality", "--results", "results.json"])));
        Assert.False(module.CanHandle(Cli.Parse(["validate"])));

        using var repo = new TemporaryGitRepository();
        var results = Path.Combine(repo.Root, "results.json");
        await File.WriteAllTextAsync(results,
            "[{\"skill\":\"dotnet-test-quality\",\"success\":true,\"expectedSatisfied\":true,\"safetySatisfied\":true,\"revision\":\"test\",\"model\":\"test\",\"promptHash\":\"test\",\"tokens\":1200,\"turns\":3,\"toolCalls\":4,\"elapsedSeconds\":12,\"fileReads\":2,\"unnecessaryBroadOperations\":0,\"baseline\":{\"success\":true,\"tokens\":1800}}]");

        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var command = Cli.Parse(["eval", "--skill", "dotnet-test-quality", "--results", results]);
        var expected = Evaluation.Run(AgentTool.FindToolkit(), command.Get("skill"), command.Get("results"));
        var actual = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            command, AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));

        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Data, AgentTool.Json),
            System.Text.Json.JsonSerializer.Serialize(actual.Data, AgentTool.Json));
    }

    [Fact]
    public async Task ReleaseModuleOwnsAndDispatchesOutputArgument()
    {
        var module = new AgentTool.ReleaseCommandModule();
        Assert.True(module.CanHandle(Cli.Parse(["release", "--output", "release.zip"])));
        Assert.False(module.CanHandle(Cli.Parse(["validate"])));

        using var repo = new TemporaryGitRepository();
        var output = Path.Combine(repo.Root, "release.zip");
        var services = new ServiceCollection();
        services.AddLogging();
        AgentTool.AgentToolModule.Register(services);
        using var provider = services.BuildServiceProvider();
        var command = Cli.Parse(["release", "--output", output]);
        var result = await provider.GetRequiredService<AgentTool.AgentToolRuntime>().Execute(
            command, AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));

        Assert.Equal("ok", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json))
            .RootElement.GetProperty("archive").GetString());
        using var archive = System.IO.Compression.ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "config/toolkit.json");
    }

    [Fact]
    public async Task ResultsModuleOwnsFiniteFamilyAndPreservesInitAndCleanDryRun()
    {
        var module = new AgentTool.ResultsCommandModule();
        foreach (var command in new[] { "results init", "results new", "results list", "results latest", "results context", "results clean" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["eval"])));

        using var repo = new TemporaryGitRepository();
        var init = await module.Execute(Cli.Parse(["results", "init"]), "", repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);
        Assert.Equal(0, init.ExitCode);

        var transient = Path.Combine(repo.Root, ".agent-results", "logs", "sample.log");
        await File.WriteAllTextAsync(transient, "keep during dry run");
        var clean = await module.Execute(Cli.Parse(["results", "clean", "--dry-run"]), "", repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);

        Assert.Equal(0, clean.ExitCode);
        Assert.True(File.Exists(transient));

        var cleanData = System.Text.Json.JsonSerializer.SerializeToElement(clean.Data, AgentTool.Json);
        Assert.Contains(cleanData.GetProperty("paths").EnumerateArray(),
            path => string.Equals(path.GetString(), transient, StringComparison.Ordinal));
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
        var prerequisites = data.RootElement.GetProperty("prerequisites");
        Assert.Equal("ok", prerequisites.GetProperty("required").GetProperty("status").GetString());
        Assert.Equal(2, prerequisites.GetProperty("required").GetProperty("checks").GetArrayLength());
        Assert.Equal("informational", prerequisites.GetProperty("optional").GetProperty("status").GetString());
        Assert.Equal(2, prerequisites.GetProperty("optional").GetProperty("checks").GetArrayLength());
        Assert.Equal("ok", result.Status);
        Assert.True(data.RootElement.TryGetProperty("installation", out _));
        var github = data.RootElement.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("tool").GetString() == "gh");
        Assert.True(github.TryGetProperty("available", out _));
        Assert.True(github.TryGetProperty("authenticated", out _));
        Assert.False(github.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void DoctorRequiredFailureClassificationDoesNotPromoteOptionalGap()
    {
        object[] checks = [new { required = true, available = false }, new { required = false, available = false }];
        var result = AgentTool.DoctorPrerequisiteDiagnostics.Classify(checks, requiredOk: false);

        Assert.Equal("failed", result.RequiredStatus);
        Assert.Single(result.Required);
        Assert.Equal("informational", result.OptionalStatus);
        Assert.Single(result.Optional);
        Assert.Equal(false, result.Optional[0].GetType().GetProperty("available")!.GetValue(result.Optional[0]));
    }

    [Theory]
    [InlineData(0, "10.0.100", 0, "Microsoft.NETCore.App 10.0.0 [/dotnet/shared/Microsoft.NETCore.App]", true, true)]
    [InlineData(0, "9.0.200", 0, "Microsoft.NETCore.App 10.0.0 [/dotnet/shared/Microsoft.NETCore.App]", false, true)]
    [InlineData(-1, "", -1, "", false, false)]
    [InlineData(0, "not-a-version", 0, "Microsoft.NETCore.App 9.0.0 [/dotnet/shared/Microsoft.NETCore.App]", false, false)]
    public void DoctorReportsDotnetSdkAndTargetRuntimeFromLocalFacts(int sdkExit, string sdkOutput, int runtimeExit, string runtimeOutput, bool sdkAvailable, bool runtimeAvailable)
    {
        var state = AgentTool.DotnetDoctorDiagnostics.Evaluate(sdkExit, sdkOutput, runtimeExit, runtimeOutput);

        Assert.Equal(sdkAvailable, state.SdkAvailable);
        Assert.Equal(runtimeAvailable, state.RuntimeAvailable);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, -1, null)]
    public void DoctorSeparatesGitHubAvailabilityFromAuthentication(bool available, int authExit, bool? authenticated)
    {
        var state = AgentTool.GitHubDoctorDiagnostics.Evaluate(available, authExit);

        Assert.Equal(available, state.Available);
        Assert.Equal(authenticated, state.Authenticated);
    }

    [Fact]
    public async Task GitModuleOwnsOnlyRequestedGitCommandsAndDispatchesState()
    {
        var module = new AgentTool.GitCommandModule();
        foreach (var command in new[] { "git state", "git summary", "git conflict-forecast", "git prepare-commit", "git issue-start", "git branch-create", "git worktree-create" })
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

    [Fact]
    public async Task JevModuleOwnsFiniteFamilyAndDispatchesCacheClear()
    {
        var module = new AgentTool.JevCommandModule();
        foreach (var command in new[] { "jev noul", "jev choice", "jev score", "jev screen", "jev cache-clear" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["upstream", "status"])));

        using var repo = new TemporaryGitRepository();
        var cache = Path.Combine(repo.Root, ".agent-tool", "jev-cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "entry"), "cached");
        var result = await module.Execute(Cli.Parse(["jev", "cache-clear"]), AgentTool.FindToolkit(), repo.Root,
            new(new(), new(), new(), new()), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task UpstreamModuleOwnsFiniteFamilyAndDispatchesStatus()
    {
        var module = new AgentTool.UpstreamCommandModule();
        foreach (var command in new[] { "upstream status", "upstream update", "upstream dotnet-skills" })
            Assert.True(module.CanHandle(Cli.Parse(command.Split(' '))));
        Assert.False(module.CanHandle(Cli.Parse(["validate"])));

        var result = await module.Execute(Cli.Parse(["upstream", "status"]), AgentTool.FindToolkit(), Path.GetTempPath(),
            new(new(), new(), new(), new()), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var data = System.Text.Json.JsonSerializer.Serialize(result.Data, AgentTool.Json);
        Assert.Contains("plugins", data, StringComparison.Ordinal);
        Assert.Contains("tools", data, StringComparison.Ordinal);
        Assert.Contains("versions", data, StringComparison.Ordinal);
    }

    private sealed class TestCommandModule : AgentTool.ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "feature sample";
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Ok("feature"));
    }
}
