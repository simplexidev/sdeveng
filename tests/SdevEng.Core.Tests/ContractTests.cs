using SdevEng;

public class ContractTests
{
    [Fact]
    public void VerificationContractPreservesSourceAndEnvironmentIdentity()
    {
        var evidence = new VerificationResult(1, "local", "build", "passed", 0, "build.log")
        { EnvironmentIdentity = "linux-x64" };
        Assert.Equal("linux-x64", (evidence with { Check = "test" }).EnvironmentIdentity);
        Assert.Equal("local", evidence.Source);
    }

    [Fact]
    public void EvidencePolicyRejectsMissingHostedEvidenceAndPreservesTimeoutDisagreement()
    {
        var local = new VerificationResult(2, "local", "build", "timed-out", 124, "local.log");
        var missing = VerificationDecisions.Evaluate("build", [local], new(true, true));
        Assert.False(missing.CanProgress);
        Assert.Contains("hosted-missing", missing.Reasons);
        var hosted = new VerificationResult(2, "hosted", "build", "cancelled", 130, "https://example.invalid/check");
        var compared = VerificationDecisions.Evaluate("build", [local, hosted], new(true, true));
        Assert.True(compared.Disagrees);
        Assert.Contains("local-timed-out", compared.Reasons);
        Assert.Contains("hosted-cancelled", compared.Reasons);
    }
}
