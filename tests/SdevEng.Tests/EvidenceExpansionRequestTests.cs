using System.Text.Json.Nodes;
using Json.Schema;
using System.Security.Cryptography;
using System.Text;

namespace SdevEng.Tests;

public sealed class EvidenceExpansionRequestTests
{
    private const string EvidenceId = "evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void RequestSchemaAcceptsRangeOrSectionAndRejectsMalformedFields()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-expansion-request.schema.json"));
        var range = JsonNode.Parse($"{{\"schemaVersion\":1,\"packId\":\"pack-1\",\"packRevision\":\"rev-1\",\"evidenceId\":\"{EvidenceId}\",\"runId\":\"run-1\",\"role\":\"reviewer\",\"startLine\":2,\"endLine\":4}}")!;
        Assert.True(schema.Evaluate(range).IsValid);
        range["startLine"] = 0;
        Assert.False(schema.Evaluate(range).IsValid);
        range["startLine"] = 2;
        range["section"] = "content.excerpt";
        Assert.False(schema.Evaluate(range).IsValid);
        range.AsObject().Remove("startLine");
        range.AsObject().Remove("endLine");
        range["section"] = "../secret";
        Assert.False(schema.Evaluate(range).IsValid);
        range["section"] = "content.excerpt";
        range.AsObject().Remove("role");
        Assert.False(schema.Evaluate(range).IsValid);
    }

    [Fact]
    public void ValidatorAcceptsIndexedIdAndRejectsUnknownStaleAndInvalidRanges()
    {
        var known = new EvidenceExpansionRequest("pack-1", "rev-1", EvidenceId, "run-1", "reviewer", 2, 4);
        Assert.True(Validate(known).IsValid);
        Assert.Equal(EvidenceExpansionRejection.UnknownEvidenceId,
            Validate(known with { EvidenceId = "evidence:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }).Rejection);
        Assert.Equal(EvidenceExpansionRejection.StalePack,
            Validate(known with { PackRevision = "old" }).Rejection);
        Assert.Equal(EvidenceExpansionRejection.MalformedRequest,
            Validate(known with { StartLine = 0 }).Rejection);
        Assert.Equal(EvidenceExpansionRejection.MalformedRequest,
            Validate(known with { StartLine = 5, EndLine = 4 }).Rejection);
        Assert.Equal(EvidenceExpansionRejection.MalformedRequest,
            Validate(known, remainingLines: 2).Rejection);
        Assert.Equal(EvidenceExpansionRejection.MalformedRequest,
            Validate(known with { Section = "../secret", StartLine = null, EndLine = null }).Rejection);
    }

    private static EvidenceExpansionValidation Validate(EvidenceExpansionRequest request, int remainingLines = 3) =>
        EvidenceExpansionRequestValidator.Validate(request, "pack-1", "rev-1", [EvidenceId], remainingLines);

    [Fact]
    public void ExpansionHonorsBoundsUnicodeTraversalAndSourceRevision()
    {
        using var repo = new TemporaryGitRepository();
        var lines = Enumerable.Repeat(new string('a', 81), 199).Append(new string('界', 22)).ToArray();
        var content = string.Join('\n', lines);
        var path = Path.Combine(repo.Root, "excerpt.txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var item = new EvidenceExpansionItem(EvidenceId, "repo:excerpt.txt", revision);
        var request = new EvidenceExpansionRequest("pack-1", "rev-1", EvidenceId, "run-1", "reviewer", 1, 200);

        var exact = RepositoryEvidenceExpander.Expand(repo.Root, request, "pack-1", "rev-1", [item], 200);
        Assert.Equal(EvidenceExpansionStatus.Expanded, exact.Status);
        Assert.Equal(16_384, exact.Utf8Bytes);
        Assert.False(exact.TokenMeasurementAvailable);
        Assert.Equal(0, RepositoryEvidenceExpander.Expand(repo.Root, request, "pack-1", "rev-1", [item], 199).Utf8Bytes);

        File.WriteAllText(path, content + "a", new UTF8Encoding(false));
        var overRevision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content + "a")));
        var over = RepositoryEvidenceExpander.Expand(repo.Root, request, "pack-1", "rev-1", [item with { SourceRevision = overRevision }], 200);
        Assert.Equal(EvidenceExpansionStatus.BudgetExceeded, over.Status);

        var traversal = RepositoryEvidenceExpander.Expand(repo.Root, request,
            "pack-1", "rev-1", [item with { LocationKey = "repo:../secret" }], 200);
        Assert.Equal(EvidenceExpansionStatus.Omitted, traversal.Status);
        var outsideRoot = RepositoryEvidenceExpander.Expand(repo.Root, request,
            "pack-1", "rev-1", [item with { LocationKey = "repo:/tmp/secret" }], 200);
        Assert.Equal(EvidenceExpansionStatus.Omitted, outsideRoot.Status);
        var stale = RepositoryEvidenceExpander.Expand(repo.Root, request,
            "pack-1", "rev-1", [item with { SourceRevision = "stale" }], 200);
        Assert.Equal(EvidenceExpansionStatus.Omitted, stale.Status);
    }
}
