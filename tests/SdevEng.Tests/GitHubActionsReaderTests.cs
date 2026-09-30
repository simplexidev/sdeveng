namespace SdevEng.Tests;

public sealed class GitHubActionsReaderTests
{
    [Fact]
    public async Task ReadsOnlyRunsForExactCommitAndPreservesRunFactsWithinLimit()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var client = new StubReadClient("""
            {"workflow_runs":[
            {"id":1,"name":"CI","status":"completed","conclusion":"success","event":"push","head_branch":"main","head_sha":"0123456789abcdef0123456789abcdef01234567","html_url":"https://github.com/o/r/actions/runs/1","created_at":"2026-01-02T03:04:05Z","updated_at":"2026-01-02T03:05:05Z"},
            {"id":2,"name":"CI","status":"failure","conclusion":"failure","event":"push","head_branch":"main","head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","html_url":"https://github.com/o/r/actions/runs/2"},
            {"id":3,"name":"CI","status":"in_progress","conclusion":null,"event":"push","head_branch":"main","head_sha":"0123456789abcdef0123456789abcdef01234567","html_url":"https://github.com/o/r/actions/runs/3"}]}
            """);

        var runs = await new GitHubActionsReader(client).ReadRunsForCommitAsync("o", "r", sha, 1);

        Assert.Equal($"https://api.github.com/repos/o/r/actions/runs?head_sha={sha}&per_page=1", Assert.Single(client.Endpoints).ToString());
        var run = Assert.Single(runs);
        Assert.Equal(sha, run.Sha);
        Assert.Equal((1L, "CI", "completed", "success"), (run.Id, run.Workflow, run.Status, run.Conclusion));
        Assert.Equal(new Uri("https://github.com/o/r/actions/runs/1"), run.Url);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), run.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:05:05Z"), run.UpdatedAt);
    }

    [Fact]
    public async Task ReadsRunsDetailFailedStepsAndBoundedFailedLogs()
    {
        var run = """{"id":81,"name":"CI","display_title":"Fix build","status":"completed","conclusion":"failure","event":"push","head_branch":"main","head_sha":"abc","html_url":"https://github.com/o/r/actions/runs/81","created_at":"2026-01-02T03:04:05Z","updated_at":"2026-01-02T03:05:05Z"}""";
        var jobs = """{"jobs":[{"id":91,"name":"build","status":"completed","conclusion":"failure","started_at":"2026-01-02T03:04:05Z","completed_at":"2026-01-02T03:05:05Z","html_url":"https://github.com/o/r/actions/runs/81/job/91","steps":[{"name":"compile","number":2,"conclusion":"failure"},{"name":"cleanup","number":3,"conclusion":"success"}]}]}""";
        var client = new StubReadClient("{\"workflow_runs\":[" + run + "]}", run, jobs, jobs, "0123456789");
        var reader = new GitHubActionsReader(client);

        var runs = await reader.ReadRunsAsync("o", "r", 5);
        var detail = await reader.ReadRunAsync("o", "r", 81, 10);
        var logs = await reader.ReadFailedLogsAsync("o", "r", 81, 5, 4);

        Assert.Equal("CI", Assert.Single(runs).Workflow);
        Assert.Equal("Fix build", runs[0].Title);
        Assert.Equal("abc", runs[0].Sha);
        Assert.Equal("failure", Assert.Single(detail.Jobs[0].FailedSteps).Conclusion);
        Assert.Equal("\n[tr", Assert.Single(logs).Log);
        Assert.Equal("https://api.github.com/repos/o/r/actions/runs?per_page=5", client.Endpoints[0].ToString());
        Assert.Equal("https://api.github.com/repos/o/r/actions/runs/81/jobs?per_page=10", client.Endpoints[2].ToString());
        Assert.Equal("https://api.github.com/repos/o/r/actions/jobs/91/logs", client.Endpoints[4].ToString());
    }

    [Fact]
    public async Task FailedLogsRequireAnExplicitPositiveRunId()
    {
        var reader = new GitHubActionsReader(new StubReadClient());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadFailedLogsAsync("o", "r", 0, 5, 100));
    }

    [Fact]
    public async Task ReadsOnlyEarliestCausalJobLogAndMarksTruncation()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var run = $$"""{"id":81,"name":"CI","status":"completed","conclusion":"failure","event":"push","head_sha":"{{sha}}","html_url":"https://github.com/o/r/actions/runs/81"}""";
        var jobs = """{"jobs":[{"id":93,"name":"later","conclusion":"failure","started_at":"2026-01-02T03:05:00Z","steps":[{"name":"compile","number":1,"conclusion":"failure"}]},{"id":91,"name":"cancelled","conclusion":"cancelled","started_at":"2026-01-02T03:03:00Z"},{"id":92,"name":"earliest","conclusion":"timed_out","started_at":"2026-01-02T03:04:00Z","steps":[{"name":"test","number":2,"conclusion":"timed_out"}]}]}""";
        var client = new StubReadClient(run, jobs, "0123456789");

        var evidence = await new GitHubActionsReader(client).ReadFailureEvidenceAsync("o", "r", 81, sha, 8);

        Assert.NotNull(evidence);
        Assert.Equal((81L, "CI", 92L, "earliest"), (evidence.RunId, evidence.RunName, evidence.JobId, evidence.JobName));
        Assert.Equal("test", Assert.Single(evidence.FailedSteps).Name);
        Assert.True(evidence.Truncated);
        Assert.Equal(8, evidence.Log.Length);
        Assert.Equal(new[] { "https://api.github.com/repos/o/r/actions/runs/81", "https://api.github.com/repos/o/r/actions/runs/81/jobs?per_page=200", "https://api.github.com/repos/o/r/actions/jobs/92/logs" }, client.Endpoints.Select(endpoint => endpoint.ToString()));
    }

    [Fact]
    public async Task RejectsRunWhoseShaDoesNotMatchBeforeFetchingJobs()
    {
        var run = """{"id":81,"name":"CI","status":"completed","conclusion":"failure","event":"push","head_sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","html_url":"https://github.com/o/r/actions/runs/81"}""";
        var client = new StubReadClient(run);
        Assert.Null(await new GitHubActionsReader(client).ReadFailureEvidenceAsync("o", "r", 81, "0123456789abcdef0123456789abcdef01234567", 100));
        Assert.Single(client.Endpoints);
    }

    [Theory]
    [InlineData("dotnet format failed", "format")]
    [InlineData("2 tests failed", "test")]
    [InlineData("error CS1234", "build")]
    [InlineData("NU1301 package restore failed", "dependency")]
    [InlineData("operation timed out", "timeout")]
    [InlineData("hosted runner lost", "infrastructure")]
    [InlineData("something failed", "unknown")]
    public void ClassifiesNarrowObservablePatterns(string log, string expected) =>
        Assert.Equal(expected, GitHubFailureClassifier.Classify(log).Class);

    [Fact]
    public void FirstOrderedClassWinsAndCancellationIsNonCausal()
    {
        Assert.Equal("format", GitHubFailureClassifier.Classify("tests failed after format check").Class);
        Assert.Equal(("timeout", "job-conclusion:timed_out"), GitHubFailureClassifier.Classify("opaque", "timed_out"));
        Assert.Equal(("unknown", null), GitHubFailureClassifier.Classify("tests failed", "cancelled"));
    }

    [Fact]
    public async Task RedactsCompleteBoundedFailureExcerptAndClassEvidence()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var run = $$"""{"id":81,"name":"CI","status":"completed","conclusion":"failure","event":"push","head_sha":"{{sha}}","html_url":"https://github.com/o/r/actions/runs/81"}""";
        var jobs = """{"jobs":[{"id":91,"name":"build","conclusion":"failure"}]}""";
        var secret = "api_key=literal-secret-123";
        var client = new StubReadClient(run, jobs, "build failed " + secret);
        var evidence = await new GitHubActionsReader(client).ReadFailureEvidenceAsync("o", "r", 81, sha, 200);
        Assert.Equal("build", evidence!.FailureClass);
        Assert.DoesNotContain("literal-secret-123", evidence.Log);
        Assert.DoesNotContain("literal-secret-123", evidence.ClassEvidence);
        Assert.Contains("[REDACTED]", evidence.Log);
    }

    sealed class StubReadClient(params string[] bodies) : IGitHubReadClient
    {
        int index;
        public List<Uri> Endpoints { get; } = [];
        public Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
        {
            Endpoints.Add(endpoint);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(bodies[index++]) });
        }
    }
}
