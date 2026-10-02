using SdevEng;

public class ContractTests
{
    [Fact]
    public void ProjectGraphValidationReportsCyclesAndUnresolvedEdgesDeterministically()
    {
        var graph = new ProjectDependencyGraph(["a", "b"], [new("a", "b"), new("b", "a"), new("b", "missing")]);
        var validation = graph.Validate();
        Assert.False(validation.IsValid);
        Assert.Equal(["a -> b -> a"], validation.Cycles);
        Assert.Equal([new ProjectDependencyEdge("b", "missing")], validation.UnresolvedEdges);
        var repeated = graph.Validate();
        Assert.Equal(validation.IsValid, repeated.IsValid);
        Assert.Equal(validation.Cycles, repeated.Cycles);
        Assert.Equal(validation.UnresolvedEdges, repeated.UnresolvedEdges);
    }

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
