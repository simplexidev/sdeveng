using SdevEng;

public class ContractTests
{
    [Fact]
    public void RelevanceRankingInputValidatesAndCapsCandidates()
    {
        var input = new RelevanceRankingInput
        {
            Query = "documentation impact",
            Candidates = [new() { Id = "docs/readme", Text = "Build instructions" }]
        };
        input.Validate();
        Assert.Throws<ArgumentException>(() => (input with
        {
            Candidates = Enumerable.Range(0, RelevanceRankingInput.CandidateLimit + 1)
                .Select(index => new RelevanceRankingCandidate { Id = $"candidate-{index}", Text = "summary" }).ToArray()
        }).Validate());
    }

    [Fact]
    public async Task AbstainingRelevanceRankingProviderReturnsNoSemanticScores()
    {
        var provider = new AbstainingRelevanceRankingProvider();
        var input = new RelevanceRankingInput
        {
            Query = "documentation impact",
            Candidates = [new() { Id = "docs/readme", Text = "Build instructions" }]
        };

        Assert.Empty(await provider.RankAsync(input));
    }

    [Fact]
    public async Task AbstainingRelevanceRankingProviderValidatesAndHonorsCancellation()
    {
        var provider = new AbstainingRelevanceRankingProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RankAsync(new RelevanceRankingInput()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RankAsync(
            new RelevanceRankingInput { Query = "query" }, cancellation.Token));
    }

    [Fact]
    public void RelevanceRankingOrderAppliesConfidenceGateAndPreservesAbstentions()
    {
        var input = new RelevanceRankingInput
        {
            Query = "query",
            Candidates = [new() { Id = "z", Text = "first" }, new() { Id = "b", Text = "second" }, new() { Id = "a", Text = "third" }, new() { Id = "tail", Text = "tail" }]
        };
        var ordered = RelevanceRankingOrder.Apply(input,
        [
            new("z", .1) { Confidence = .79 },
            new("b", .4) { Confidence = .80 },
            new("a", .4) { Confidence = .81 },
            new("tail", .9)
        ]);
        Assert.Equal(["a", "b", "z", "tail"], ordered.Select(candidate => candidate.Id));
        Assert.Equal(input.Candidates.Select(candidate => candidate.Id), RelevanceRankingOrder.Apply(input, []).Select(candidate => candidate.Id));
        Assert.Equal(input.Candidates.Select(candidate => candidate.Id), RelevanceRankingOrder.Apply(input, null).Select(candidate => candidate.Id));
    }

    [Fact]
    public void RelevanceRankingOrderAbstainsOnMalformedScoresAndRejectsInvalidPolicy()
    {
        var input = new RelevanceRankingInput
        {
            Query = "query",
            Candidates = [new() { Id = "x", Text = "x" }, new() { Id = "y", Text = "y" }, new() { Id = "z", Text = "z" }]
        };
        var scores = new RelevanceRankingScore[]
        {
            new("x", .9) { Confidence = double.NaN },
            new("x", .8) { Confidence = .9 },
            new("y", 1.1) { Confidence = .9 },
            new("missing", .9) { Confidence = .9 },
            new("z", .9) { Confidence = 1.1 }
        };
        Assert.Equal(["x", "y", "z"], RelevanceRankingOrder.Apply(input, scores).Select(candidate => candidate.Id));
        Assert.Throws<ArgumentException>(() => RelevanceRankingOrder.Apply(input, [], new() { MinConfidence = double.NaN }));
        Assert.Throws<ArgumentException>(() => RelevanceRankingOrder.Apply(input, [], new() { MinConfidence = 1.01 }));
    }

    [Fact]
    public void RelevanceRankingKeepsRequiredEvidenceAheadOfHighScoringOptionalCandidates()
    {
        var input = new RelevanceRankingInput
        {
            Query = "query",
            Candidates =
            [
                new() { Id = "optional", Text = "optional", },
                new() { Id = "project", Text = "project metadata", MustInclude = true, Category = RelevanceRankingCategory.OwningProject },
                new() { Id = "reference", Text = "explicit symbol", MustInclude = true, Category = RelevanceRankingCategory.DeterministicReference }
            ]
        };
        var result = RelevanceRankingOrder.ApplySelections(input, [new("optional", 1) { Confidence = 1 }]);
        Assert.Equal(["project", "reference", "optional"], result.Select(item => item.Id));
        Assert.Equal(RelevanceRankingCategory.OwningProject, result[0].Category);
        Assert.Equal(RelevanceRankingCategory.DeterministicReference, result[1].Category);
        Assert.Equal(RelevanceRankingCategory.SemanticRanked, result[2].Category);
        Assert.All(result.Take(2), item => Assert.True(item.MustInclude));
    }

    [Fact]
    public void RelevanceRankingAbstentionCollapsesDuplicateIdsAndLabelsEverySelection()
    {
        var input = new RelevanceRankingInput
        {
            Query = "query",
            Candidates =
            [
                new() { Id = "required", Text = "first", MustInclude = true, Category = RelevanceRankingCategory.CandidateTest },
                new() { Id = "required", Text = "duplicate" },
                new() { Id = "optional", Text = "optional" }
            ]
        };
        var result = RelevanceRankingOrder.ApplySelections(input, []);
        Assert.Equal(["required", "optional"], result.Select(item => item.Id));
        Assert.Equal("first", result[0].Text);
        Assert.Equal(RelevanceRankingCategory.CandidateTest, result[0].Category);
        Assert.Equal(RelevanceRankingCategory.SemanticAbstained, result[1].Category);
        Assert.Equal(result, RelevanceRankingOrder.ApplySelections(input, []));
    }

    [Fact]
    public void RelevanceRankingRetainsEntireMandatoryPackAtCandidateBoundary()
    {
        var input = new RelevanceRankingInput
        {
            Query = "query",
            Candidates = Enumerable.Range(0, RelevanceRankingInput.CandidateLimit)
                .Select(index => new RelevanceRankingCandidate { Id = $"required-{index}", Text = "evidence", MustInclude = true, Category = RelevanceRankingCategory.DeterministicReference }).ToArray()
        };
        var result = RelevanceRankingOrder.ApplySelections(input, [new("required-0", 1) { Confidence = 1 }]);
        Assert.Equal(RelevanceRankingInput.CandidateLimit, result.Count);
        Assert.All(result, item => Assert.True(item.MustInclude));
        Assert.Equal(input.Candidates.Select(item => item.Id), result.Select(item => item.Id));
    }

    [Fact]
    public void RelevanceRankingRejectsMustIncludeWithoutDeterministicProvenance()
    {
        var input = new RelevanceRankingInput { Query = "query", Candidates = [new() { Id = "required", Text = "evidence", MustInclude = true }] };
        Assert.Throws<ArgumentException>(input.Validate);
    }

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
