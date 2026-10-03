using System.Text.Json.Nodes;
using Json.Schema;

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
}
