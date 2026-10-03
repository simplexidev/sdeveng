namespace SdevEng;

/// <summary>A request for one bounded excerpt from an item in a specific evidence pack.</summary>
public sealed record EvidenceExpansionRequest(
    string PackId,
    string PackRevision,
    string EvidenceId,
    string RunId,
    string Role,
    int? StartLine = null,
    int? EndLine = null,
    string? Section = null);

public enum EvidenceExpansionRejection
{
    None,
    MalformedRequest,
    StalePack,
    UnknownEvidenceId
}

public sealed record EvidenceExpansionValidation(EvidenceExpansionRejection Rejection)
{
    public bool IsValid => Rejection == EvidenceExpansionRejection.None;
}

/// <summary>Validates expansion requests against the current pack index without loading excerpts.</summary>
public static class EvidenceExpansionRequestValidator
{
    public static EvidenceExpansionValidation Validate(
        EvidenceExpansionRequest request,
        string currentPackId,
        string currentPackRevision,
        IEnumerable<string> indexedEvidenceIds,
        int remainingRoleAllowanceLines)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexedEvidenceIds);

        var hasRange = request.StartLine.HasValue || request.EndLine.HasValue;
        var hasSection = !string.IsNullOrWhiteSpace(request.Section);
        if (string.IsNullOrWhiteSpace(request.PackId) ||
            string.IsNullOrWhiteSpace(request.PackRevision) ||
            string.IsNullOrWhiteSpace(request.EvidenceId) ||
            string.IsNullOrWhiteSpace(request.RunId) ||
            string.IsNullOrWhiteSpace(request.Role) ||
            remainingRoleAllowanceLines < 0 ||
            hasRange == hasSection ||
            (hasRange && (!request.StartLine.HasValue || !request.EndLine.HasValue || request.StartLine < 1 || request.EndLine < request.StartLine || (long)request.EndLine - request.StartLine + 1 > remainingRoleAllowanceLines)) ||
            (hasSection && (request.Section!.Any(char.IsControl) || request.Section!.Split('/', '\\').Any(segment => segment is "." or ".."))))
        {
            return new(EvidenceExpansionRejection.MalformedRequest);
        }

        if (!string.Equals(request.PackId, currentPackId, StringComparison.Ordinal) ||
            !string.Equals(request.PackRevision, currentPackRevision, StringComparison.Ordinal))
        {
            return new(EvidenceExpansionRejection.StalePack);
        }

        return indexedEvidenceIds.Contains(request.EvidenceId, StringComparer.Ordinal)
            ? new(EvidenceExpansionRejection.None)
            : new(EvidenceExpansionRejection.UnknownEvidenceId);
    }
}
