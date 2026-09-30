using System.Text.Json;
using Json.Schema;

namespace SdevEng.Tests;

public class VerificationResultsTests
{
    [Theory]
    [InlineData("passed", "passed", true, false)]
    [InlineData("passed", "failed", false, true)]
    [InlineData("failed", "passed", false, true)]
    [InlineData("failed", "failed", false, false)]
    public void EvaluationDetectsDisagreementAndGatesFailures(string localStatus, string hostedStatus, bool canProgress, bool disagrees)
    {
        var results = new[] { Evidence("local", localStatus), Evidence("hosted", hostedStatus) };
        var decision = VerificationResults.Evaluate("build", results, new(true, true));
        Assert.Equal(canProgress, decision.CanProgress);
        Assert.Equal(disagrees, decision.Disagrees);
        Assert.Equal(disagrees, decision.Reasons.Contains("local-hosted-disagreement"));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/verification-decision.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(decision, AgentTool.Json)).IsValid);
    }

    [Fact]
    public void EvaluationRequiresConfiguredSourcesAndRejectsAmbiguousEvidence()
    {
        var local = Evidence("local", "passed");
        Assert.True(VerificationResults.Evaluate("build", [local], new(true, false)).CanProgress);
        var missing = VerificationResults.Evaluate("build", [local], new(true, true));
        Assert.False(missing.CanProgress);
        Assert.Contains("hosted-missing", missing.Reasons);
        Assert.False(VerificationResults.Evaluate("build", [], new(false, false)).CanProgress);
        Assert.Throws<ArgumentException>(() => VerificationResults.Evaluate("build", [local, local], new(true, false)));
        Assert.Throws<ArgumentException>(() => VerificationResults.Evaluate("test", [local], new(true, false)));
    }

    static VerificationResult Evidence(string source, string status) => new(1, source, "build", status, status == "passed" ? 0 : 1, "https://example.com/evidence");

    [Theory]
    [InlineData(0, "passed")]
    [InlineData(1, "failed")]
    public void LocalProcessOutcomeMapsToVerificationContract(int exitCode, string status)
    {
        var artifact = Path.Combine(Path.GetTempPath(), "verification.log");
        var result = VerificationResults.FromLocal("dotnet", ["test", "project.csproj"], exitCode, artifact);
        Assert.Equal("local", result.Source);
        Assert.Equal("dotnet test project.csproj", result.Check);
        Assert.Equal(status, result.Status);
        Assert.Equal(artifact, result.Artifact);
        Assert.Equal(Environment.MachineName, result.EnvironmentIdentity);
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/verification-result.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(result, AgentTool.Json)).IsValid);
        var invalid = JsonSerializer.SerializeToNode(result, AgentTool.Json)!;
        invalid["status"] = status == "passed" ? "failed" : "passed";
        Assert.False(schema.Evaluate(invalid).IsValid);
    }

    [Theory]
    [InlineData("success", "passed", 0)]
    [InlineData("failure", "failed", 1)]
    [InlineData("timed_out", "failed", 1)]
    [InlineData("cancelled", "failed", 1)]
    [InlineData("action_required", "failed", 1)]
    public void HostedCheckMapsToVerificationContract(string conclusion, string status, int exitCode)
    {
        const string sha = "0123456789012345678901234567890123456789";
        var url = new Uri("https://github.com/o/r/runs/12");
        var result = VerificationResults.FromGitHubCheck(new GitHubCheck(12, "build", "completed", conclusion, url), "o", "r", sha);
        Assert.Equal("hosted", result.Source);
        Assert.Equal("build", result.Check);
        Assert.Equal(status, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(url.AbsoluteUri, result.Artifact);
        Assert.Equal($"github:o/r@{sha}", result.EnvironmentIdentity);
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/verification-result.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(result, AgentTool.Json)).IsValid);
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("completed", "neutral")]
    [InlineData("completed", "skipped")]
    public void HostedCheckRejectsIndeterminateOutcome(string state, string? conclusion)
    {
        var check = new GitHubCheck(12, "build", state, conclusion, new Uri("https://github.com/o/r/runs/12"));
        Assert.Throws<ArgumentException>(() => VerificationResults.FromGitHubCheck(check, "o", "r", "0123456789012345678901234567890123456789"));
    }

    [Fact]
    public void LocalMappingRejectsInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() => VerificationResults.FromLocal("", [], 0, "log"));
        Assert.Throws<ArgumentOutOfRangeException>(() => VerificationResults.FromLocal("dotnet", [], -1, "log"));
    }

    [Fact]
    public async Task LocalCommandProducesVerificationAlongsideExistingReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-verification-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await AgentTool.RunArtifact("dotnet", ["--version"], AgentTool.FindToolkit(), directory, new OutputSettings());
            Assert.Equal(0, result.ExitCode);
            var report = Assert.IsType<ProcessReport>(result.Data);
            Assert.NotNull(report.Verification);
            Assert.Equal("passed", report.Verification.Status);
            Assert.Equal(report.Artifact, report.Verification.Artifact);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/verification-result.schema.json"));
            Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(report.Verification, AgentTool.Json)).IsValid);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
