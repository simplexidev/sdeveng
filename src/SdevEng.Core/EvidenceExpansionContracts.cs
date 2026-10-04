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
    string? Section = null,
    string? RequestId = null,
    string? ParentRequestId = null);

public enum EvidenceExpansionRejection
{
    None,
    MalformedRequest,
    StalePack,
    UnknownEvidenceId,
    RecursiveExpansion,
    WrongRun,
    ConflictingReplay
}

public sealed record EvidenceExpansionValidation(EvidenceExpansionRejection Rejection)
{
    public bool IsValid => Rejection == EvidenceExpansionRejection.None;
}

/// <summary>Current indexed source metadata required to resolve one expansion.</summary>
public sealed record EvidenceExpansionItem(string Id, string LocationKey, string SourceRevision);

public enum EvidenceExpansionStatus { Expanded, Omitted, BudgetExceeded, Rejected }

/// <summary>One bounded excerpt; token measurement is unavailable because no exact tokenizer is bound.</summary>
public sealed record EvidenceExpansionResult(EvidenceExpansionStatus Status, string? EvidenceId, string? Excerpt, int? StartLine, int? EndLine, int Utf8Bytes, bool TokenMeasurementAvailable, EvidenceExpansionRejection Rejection = EvidenceExpansionRejection.None);

/// <summary>Recorded retries carry metadata only; excerpts are never persisted in run events.</summary>
public sealed record EvidenceExpansionOutcome(EvidenceExpansionResult Result, string RequestId, string Digest,
    string Reason, bool Recorded, bool Replay = false, bool ContentAvailable = false);

/// <summary>Validates expansion requests against the current pack index without loading excerpts.</summary>
public static class EvidenceExpansionRequestValidator
{
    public static bool ValidRequestReference(string? value) => value is null ||
        value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public static EvidenceExpansionValidation Validate(
        EvidenceExpansionRequest request,
        string currentPackId,
        string currentPackRevision,
        IEnumerable<string> indexedEvidenceIds,
        int remainingRoleAllowanceLines)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(indexedEvidenceIds);

        if (!ValidRequestReference(request.RequestId) || !ValidRequestReference(request.ParentRequestId))
            return new(EvidenceExpansionRejection.MalformedRequest);
        if (request.ParentRequestId is not null)
            return new(EvidenceExpansionRejection.RecursiveExpansion);

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
