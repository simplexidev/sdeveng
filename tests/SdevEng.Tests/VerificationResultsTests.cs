using System.Text.Json;
using Json.Schema;

namespace SdevEng.Tests;

public class VerificationResultsTests
{
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
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/verification-result.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(result, AgentTool.Json)).IsValid);
        var invalid = JsonSerializer.SerializeToNode(result, AgentTool.Json)!;
        invalid["status"] = status == "passed" ? "failed" : "passed";
        Assert.False(schema.Evaluate(invalid).IsValid);
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
