using SdevEng;
using System.Text.Json;

public class ContractTests
{
    private static PromptComponent Component(PromptComponentId id) => new(id, "implementer", "step-policy", $"content/{id}", $"sha256:{id}",
        ResponseSchema: id == PromptComponentId.OutputContract ? "schema:worker-response/v1" : null);

    [Fact]
    public void PromptManifestDefinesSystemAndRoleAndAllowsEmptyOptionalCategories()
    {
        var manifest = new PromptManifest(PromptManifest.CurrentSchemaVersion,
        [
            Component(PromptComponentId.System),
            Component(PromptComponentId.Role),
            Component(PromptComponentId.Request),
            Component(PromptComponentId.OutputContract)
        ]);

        manifest.Validate();
        Assert.Equal(PromptComponentId.System, manifest.Components[0].Id);
        Assert.Equal(PromptComponentId.Role, manifest.Components[1].Id);
        Assert.Equal("implementer", manifest.Components[1].Role);
        Assert.Equal("content/Role", manifest.Components[1].ContentReference);
        Assert.Equal("sha256:Role", manifest.Components[1].ContentHash);
    }

    [Fact]
    public void PromptManifestRejectsMissingEmptyDuplicateUnknownAndOutOfOrderComponents()
    {
        var minimal = new[] { Component(PromptComponentId.System), Component(PromptComponentId.Role), Component(PromptComponentId.Request), Component(PromptComponentId.OutputContract) };
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, minimal.Skip(1).ToArray()).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [minimal[0] with { Provenance = " " }, .. minimal.Skip(1)]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [.. minimal, minimal[0]]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [minimal[0] with { Id = (PromptComponentId)999 }, .. minimal.Skip(1)]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [minimal[0], minimal[1], Component(PromptComponentId.Evidence), minimal[2], minimal[3]]).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PromptManifest(2, minimal).Validate());
    }

    [Fact]
    public void PromptManifestRepresentsSkillMetadataLoadedInstructionsAndUnloadedReferences()
    {
        var manifest = new PromptManifest(1,
        [
            Component(PromptComponentId.System),
            Component(PromptComponentId.Role),
            new(PromptComponentId.SkillMetadata, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/metadata", "sha256:metadata"),
            new(PromptComponentId.SkillInstructions, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/SKILL.md", "sha256:instructions"),
            new(PromptComponentId.SkillReferences, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/references/guide.md", "sha256:reference", IsLoaded: false),
            Component(PromptComponentId.Request),
            Component(PromptComponentId.OutputContract)
        ]);

        manifest.Validate();
        Assert.Equal([PromptComponentId.SkillMetadata, PromptComponentId.SkillInstructions, PromptComponentId.SkillReferences],
            manifest.Components.Where(component => component.Id is PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions or PromptComponentId.SkillReferences).Select(component => component.Id));
        Assert.False(manifest.Components.Single(component => component.Id == PromptComponentId.SkillReferences).IsLoaded);
    }

    [Fact]
    public void PromptManifestRejectsLoadedReferencesDuplicateSkillIdentityAndUnstableSkillOrder()
    {
        var prefix = new[] { Component(PromptComponentId.System), Component(PromptComponentId.Role) };
        var suffix = new[] { Component(PromptComponentId.Request), Component(PromptComponentId.OutputContract) };
        var metadata = new PromptComponent(PromptComponentId.SkillMetadata, "implementer", "skill", "skills/alpha/1.0/metadata", "sha256:a");
        var otherMetadata = metadata with { ContentReference = "skills/beta/1.0/metadata" };
        var reference = new PromptComponent(PromptComponentId.SkillReferences, "implementer", "skill", "skills/alpha/1.0/reference.md", "sha256:r", IsLoaded: false);

        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [.. prefix, reference with { IsLoaded = true }, .. suffix]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [.. prefix, metadata, metadata, .. suffix]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1, [.. prefix, otherMetadata, metadata, .. suffix]).Validate());
    }

    [Fact]
    public void PromptManifestDefinesTypedToolsAndPinsOutputSchema()
    {
        var tools = Component(PromptComponentId.Tools) with
        {
            Tools = [new("repo.read", "Read repository file", "Read a bounded repository file.", "schema:repo-read/v1")]
        };
        var manifest = new PromptManifest(1,
        [Component(PromptComponentId.System), Component(PromptComponentId.Role), Component(PromptComponentId.Request), tools, Component(PromptComponentId.OutputContract)]);
        manifest.Validate();
        Assert.Equal(PromptComponentId.Tools, manifest.Components[3].Id);
        Assert.Equal("repo.read", Assert.Single(manifest.Components[3].Tools!).Id);
        Assert.Throws<ArgumentException>(() => new PromptManifest(1,
            [Component(PromptComponentId.System), Component(PromptComponentId.Role), Component(PromptComponentId.Request),
             tools with { Tools = [tools.Tools![0], tools.Tools[0]] }, Component(PromptComponentId.OutputContract)]).Validate());
        Assert.Throws<ArgumentException>(() => new PromptManifest(1,
            [Component(PromptComponentId.System), Component(PromptComponentId.Role), Component(PromptComponentId.Request),
             Component(PromptComponentId.OutputContract) with { ResponseSchema = null }]).Validate());
    }

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
