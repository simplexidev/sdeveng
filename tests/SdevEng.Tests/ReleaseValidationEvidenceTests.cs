using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class ReleaseValidationEvidenceTests
{
    const string Commit = "0123456789abcdef0123456789abcdef01234567";
    static string SchemaPath => Path.Combine(AgentTool.FindToolkit(), "schemas/release-validation-evidence.schema.json");

    [Fact]
    public void SerializationIsCanonicalAndConformsToSchema()
    {
        var first = ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [
            new("zeta", "passed", Artifacts: [new("reports/z.trx", new string('a', 64)), new("reports/a.trx", new string('b', 64))]),
            new("alpha", "failed", "tests failed")]);
        var second = ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [
            new("alpha", "failed", "tests failed"),
            new("zeta", "passed", Artifacts: [new("reports/a.trx", new string('b', 64)), new("reports/z.trx", new string('a', 64))])]);
        var bytes = ReleaseValidationEvidence.Serialize(first);
        Assert.Equal(bytes, ReleaseValidationEvidence.Serialize(second));
        var schema = JsonSchema.FromFile(SchemaPath);
        Assert.True(schema.Evaluate(JsonNode.Parse(bytes), new() { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Fact]
    public void CanonicalFixtureMatchesProducerAndValidatesAgainstSchema()
    {
        var fixturePath = Path.Combine(AgentTool.FindToolkit(), "tests/fixtures/release-validation/manifest.json");
        var fixture = File.ReadAllBytes(fixturePath);
        var manifest = ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [
            new("tests", "not-observed", "trusted evidence unavailable", [new("logical:test-results", new string('a', 64))]),
            new("build", "passed")]);
        Assert.Equal(JsonNode.Parse(fixture)!.ToJsonString(AgentTool.Json), JsonNode.Parse(ReleaseValidationEvidence.Serialize(manifest))!.ToJsonString(AgentTool.Json));
        var schema = JsonSchema.FromFile(SchemaPath);
        Assert.True(schema.Evaluate(JsonNode.Parse(fixture), new() { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("unsupported")]
    [InlineData("not-observed")]
    public void FourStatusesRemainDistinct(string status)
    {
        var reason = status == "passed" ? null : "evidence unavailable or gate failed";
        var manifest = ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [new("build", status, reason)]);
        Assert.Equal(status, manifest.Gates.Single().Status);
        ReleaseValidationEvidence.Validate(manifest);
    }

    [Fact]
    public void RejectsDuplicateRecordsInvalidReasonsAndUnsafeArtifactReferences()
    {
        Assert.Throws<ArgumentException>(() => ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [new("build", "passed"), new("build", "passed")]));
        Assert.Throws<ArgumentException>(() => ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [new("build", "unsupported")]));
        Assert.Throws<ArgumentException>(() => ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [new("build", "passed", Artifacts: [new("../secret", new string('a', 64))])]));
        Assert.Throws<ArgumentException>(() => ReleaseValidationEvidence.Create("simplexidev/sdeveng", Commit, "release", [new("build", "passed", Artifacts: [new("logs/build.txt", new string('a', 64)), new("logs/build.txt", new string('b', 64))])]));
    }

    [Fact]
    public void SerializerRejectsNonCanonicalInputOrdering()
    {
        var manifest = new ReleaseValidationManifest(1, "simplexidev/sdeveng", Commit, "release", [new("z", "passed"), new("a", "passed")]);
        Assert.Throws<ArgumentException>(() => ReleaseValidationEvidence.Serialize(manifest));
    }
}

public sealed class ReleaseValidationPublisherTests
{
    [Fact]
    public async Task PublishesDeterministicCurrentCommitBoundManifestThroughRegisteredCommand()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/simplexidev/sdeveng.git");
        var expectedCommit = repo.Run("rev-parse", "HEAD").Trim();
        var command = Cli.Parse(["release", "evidence", "--profile", "release", "--output", "artifacts/evidence.json"]);
        var first = await AgentTool.Execute(command, AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var firstBytes = await File.ReadAllBytesAsync(Path.Combine(repo.Root, "artifacts/evidence.json"));
        var second = await AgentTool.Execute(command, AgentTool.FindToolkit(), repo.Root, new(new(), new(), new(), new()));
        var secondBytes = await File.ReadAllBytesAsync(Path.Combine(repo.Root, "artifacts/evidence.json"));
        Assert.Equal("ok", first.Status);
        Assert.Equal("ok", second.Status);
        Assert.Equal(firstBytes, secondBytes);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ReleaseValidationManifest>(firstBytes, AgentTool.Json)!;
        Assert.Equal("simplexidev/sdeveng", manifest.Repository);
        Assert.Equal(expectedCommit, manifest.CommitSha);
        Assert.Equal("unsupported", manifest.Gates.Single(g => g.GateId == "reproducibility").Status);
        Assert.All(manifest.Gates.Where(g => g.GateId != "reproducibility"), gate => Assert.Equal("not-observed", gate.Status));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/release-validation-evidence.schema.json"));
        Assert.True(schema.Evaluate(JsonNode.Parse(firstBytes), new() { OutputFormat = OutputFormat.List }).IsValid);
    }

    [Fact]
    public async Task PublishesOnlyEvidenceBoundToCurrentCommitAndTag()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/simplexidev/sdeveng.git");
        var sha = repo.Run("rev-parse", "HEAD").Trim();
        var supplied = ReleaseValidationEvidence.Create("simplexidev/sdeveng", sha, "release",
            [new("build-linux", "passed"), new("tests-linux", "passed")]) with
        { Tag = "v3.0.0" };
        File.WriteAllBytes(Path.Combine(repo.Root, "observed.json"), ReleaseValidationEvidence.Serialize(supplied));

        var result = await ReleaseValidationPublisher.PublishAsync(repo.Root, "release", "artifacts/evidence.json",
            evidenceFile: "observed.json", expectedTag: "v3.0.0");
        Assert.All(result.Manifest.Gates, gate => Assert.Equal("passed", gate.Status));
        Assert.Equal("v3.0.0", result.Manifest.Tag);

        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseValidationPublisher.PublishAsync(
            repo.Root, "release", "artifacts/rejected.json", evidenceFile: "observed.json", expectedTag: "v3.0.1"));
        Assert.False(File.Exists(Path.Combine(repo.Root, "artifacts/rejected.json")));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/tmp/outside.json")]
    public async Task RejectsUnsafePublicationPathsWithoutWritingOutsideRepository(string output)
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/simplexidev/sdeveng.git");
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseValidationPublisher.PublishAsync(repo.Root, "release", output));
    }
}
