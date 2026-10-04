using System.Security.Cryptography;
using System.Text.Json;

namespace SdevEng;

/// <summary>One Run-owned expansion, with sequential retry identity and metadata-only replay.</summary>
public sealed class RunEvidenceExpansion
{
    private readonly Func<string, EvidenceExpansionRequest, string, string, IEnumerable<EvidenceExpansionItem>, int, EvidenceExpansionResult> expand;

    public RunEvidenceExpansion() : this(RepositoryEvidenceExpander.Expand) { }

    internal RunEvidenceExpansion(Func<string, EvidenceExpansionRequest, string, string, IEnumerable<EvidenceExpansionItem>, int, EvidenceExpansionResult> expand)
    {
        this.expand = expand;
    }

    public EvidenceExpansionOutcome Expand(string repositoryRoot, string runArtifactRoot, Guid runId,
        EvidenceExpansionRequest request, string currentPackId, string currentPackRevision,
        IEnumerable<EvidenceExpansionItem> indexedItems, int remainingAllowanceLines, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexedItems);
        EvidenceExpansionOutcome Reject(EvidenceExpansionRejection rejection, string id = "", string digest = "") =>
            new(new(EvidenceExpansionStatus.Rejected, request.EvidenceId, null, null, null, 0, false, rejection), id, digest, rejection.ToString(), false);

        if (runId == Guid.Empty || !Guid.TryParseExact(request.RunId, "D", out var requestedRun) || requestedRun != runId)
            return Reject(EvidenceExpansionRejection.WrongRun);

        var items = indexedItems.ToArray();
        var matching = items.Where(item => item.Id == request.EvidenceId).ToArray();
        var allowance = Math.Min(remainingAllowanceLines, RepositoryEvidenceExpander.MaximumLines);
        // Fixed property order, canonical Run spelling and sorted indexed source tuples make the digest stable.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId = runId.ToString("D"),
            request.PackId,
            request.PackRevision,
            request.EvidenceId,
            request.Role,
            request.StartLine,
            request.EndLine,
            request.Section,
            request.RequestId,
            request.ParentRequestId,
            suppliedRequestId = requestId,
            currentPackId,
            currentPackRevision,
            sources = matching.OrderBy(item => item.LocationKey, StringComparer.Ordinal).ThenBy(item => item.SourceRevision, StringComparer.Ordinal).ToArray(),
            allowance
        });
        var digest = Convert.ToHexStringLower(SHA256.HashData(canonical));
        var stableId = requestId ?? request.RequestId ?? digest;
        var invalidIdentity = !EvidenceExpansionRequestValidator.ValidRequestReference(stableId) ||
            requestId is not null && request.RequestId is not null && requestId != request.RequestId;
        if (invalidIdentity) stableId = digest;
        var existing = LocalRunEventStore.Read(runArtifactRoot, runId).FirstOrDefault(item =>
            item.GetProperty("eventType").GetString() == "evidence-expansion" && item.GetProperty("requestId").GetString() == stableId);
        if (existing.ValueKind != JsonValueKind.Undefined)
        {
            if (!existing.TryGetProperty("requestDigest", out var recordedDigest) || recordedDigest.GetString() != digest)
                return Reject(EvidenceExpansionRejection.ConflictingReplay, stableId, digest);
            var status = existing.GetProperty("status").GetString() switch
            {
                "accepted" => EvidenceExpansionStatus.Expanded,
                "omitted" => EvidenceExpansionStatus.Omitted,
                "budget-exceeded" => EvidenceExpansionStatus.BudgetExceeded,
                _ => EvidenceExpansionStatus.Rejected
            };
            int? Line(string name) => existing.GetProperty(name).ValueKind == JsonValueKind.Null ? null : existing.GetProperty(name).GetInt32();
            return new(new(status, existing.GetProperty("evidenceId").GetString(), null, Line("startLine"), Line("endLine"),
                existing.GetProperty("actualBytes").GetInt32(), false, Enum.Parse<EvidenceExpansionRejection>(existing.GetProperty("rejection").GetString()!)),
                stableId, digest, existing.GetProperty("reason").GetString()!, true, true, false);
        }

        var validation = EvidenceExpansionRequestValidator.Validate(request, currentPackId, currentPackRevision,
            items.Select(item => item.Id), remainingAllowanceLines);
        var result = invalidIdentity
            ? Reject(EvidenceExpansionRejection.MalformedRequest).Result
            : !validation.IsValid ? Reject(validation.Rejection).Result
            : matching.Length != 1 ? new(EvidenceExpansionStatus.Omitted, request.EvidenceId, null, null, null, 0, false)
            : expand(repositoryRoot, request, currentPackId, currentPackRevision, items, remainingAllowanceLines);
        var reason = result.Status switch
        {
            EvidenceExpansionStatus.Expanded => "expanded",
            EvidenceExpansionStatus.Rejected => result.Rejection.ToString(),
            EvidenceExpansionStatus.BudgetExceeded => "reader-budget-exceeded",
            _ => request.Section is not null ? "unsupported-section" : "source-path-or-range-unavailable"
        };
        var outcome = new EvidenceExpansionOutcome(result, stableId, digest, reason, true, ContentAvailable: result.Excerpt is not null);
        // A failed append throws; it must never be reported as expanded success.
        LocalRunEventStore.AppendEvidenceExpansion(runArtifactRoot, runId, request, matching.Length == 1 ? matching[0].SourceRevision : null, outcome);
        return outcome;
    }
}
