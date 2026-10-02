using System.Text.Json;

namespace SdevEng.Tests;

public sealed class VerificationProgressionTests
{
    [Theory]
    [InlineData("passed", "passed", true, false)]
    [InlineData("passed", "failed", false, true)]
    public async Task ExactCommitPolicyConsumesHostedAndLocalEvidence(string localResult, string hostedResult, bool progresses, bool disagrees)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdeveng-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sha = new string('a', 40);
            var required = VerificationEvidenceFiles.ReadPolicy(Path.Combine(AgentTool.FindToolkit(), "config", "verification-evidence-policy.json")).RequiredChecks;
            object Check(string source, string name, string status) => new
            {
                schemaVersion = source == "hosted" ? 2 : 1,
                source,
                check = name,
                status,
                exitCode = status == "passed" ? 0 : 1,
                artifact = "test:" + name,
                environmentIdentity = source == "local" ? "local:test" : "github:ubuntu:100"
            };
            var hosted = Path.Combine(directory, "hosted.json");
            var local = Path.Combine(directory, "local.json");
            File.WriteAllText(hosted, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                repository = "simplexidev/sdeveng",
                commitSha = sha,
                validationTier = "final-pr",
                runner = "ubuntu-latest",
                providerRunId = "100",
                attemptOrdinal = 1,
                pullRequestId = "60",
                checks = required.Select(name => Check("hosted", name, name == "build" ? hostedResult : "passed")),
                notObserved = Array.Empty<string>()
            }));
            File.WriteAllText(local, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                commitSha = sha,
                checks = required.Select(name => Check("local", name, name == "build" ? localResult : "passed"))
            }));
            var command = Cli.Parse(["verification", "decide", "--hosted-file", hosted, "--local-file", local, "--commit", sha]);
            var result = await CommandTestRuntime.Execute(command, AgentTool.FindToolkit(), directory, new(new(), new(), new(), new()));
            Assert.Equal(progresses, result.ExitCode == 0);
            var data = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            Assert.Equal(disagrees, data["disagreements"]!.AsArray().Count > 0);
            Assert.Equal(required.Count, data["decisions"]!.AsArray().Count);
            Assert.Throws<InvalidDataException>(() => VerificationEvidenceFiles.ReadHosted(hosted, new string('b', 40)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
