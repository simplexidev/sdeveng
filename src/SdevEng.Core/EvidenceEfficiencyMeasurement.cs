namespace SdevEng;

/// <summary>A candidate or selected evidence item with its stable source revision and exact supplied measurement.</summary>
public sealed record EvidenceEfficiencyItem(string EvidenceId, string SourceRevision, string Text,
    long? Utf8Bytes, long? Tokens, string? FileLocationKey = null, string? SymbolLocationKey = null);

/// <summary>A privacy-safe observation that evidence was reduced or exceeded its budget.</summary>
public sealed record EvidenceEfficiencyEvent(string EventType, string EvidenceId, string SourceRevision,
    string Reason, long OriginalBytes, long ResultBytes, long? OriginalTokens = null, long? ResultTokens = null);

/// <summary>Versioned counts for the evidence candidates and selections supplied for one role.</summary>
public sealed record EvidenceEfficiencyMeasurement(int SchemaVersion, string Kind,
    IReadOnlyList<EvidenceEfficiencyItem> Candidates, IReadOnlyList<EvidenceEfficiencyItem> Selected,
    int CandidateFileCount, int SelectedFileCount, int CandidateSymbolCount, int SelectedSymbolCount,
    long CandidateBytes, long? CandidateTokens, long SelectedBytes, long? SelectedTokens,
    decimal? SelectionRatio, string? SelectionRatioUnavailableReason,
    string Role, long RoleInputBudgetTokens, long? RenderedInputTokens,
    decimal? UtilizationPercent, string? UtilizationUnavailableReason,
    IReadOnlyList<EvidenceEfficiencyEvent> TruncationEvents, IReadOnlyList<EvidenceEfficiencyEvent> OverflowEvents)
{
    public const int CurrentSchemaVersion = 2;
    public const string ResultKind = "evidence-efficiency-measurement";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Kind != ResultKind || Candidates is null || Selected is null ||
            string.IsNullOrWhiteSpace(Role) || RoleInputBudgetTokens < 0 || RenderedInputTokens < 0 ||
            TruncationEvents is null || OverflowEvents is null ||
            Candidates.Concat(Selected).Any(item => item is null || string.IsNullOrWhiteSpace(item.EvidenceId) ||
                string.IsNullOrWhiteSpace(item.SourceRevision) || item.Text is null || item.Utf8Bytes < 0 || item.Tokens < 0))
            throw new ArgumentException("Invalid evidence efficiency measurement.");
        ValidateEvents(TruncationEvents, "truncation");
        ValidateEvents(OverflowEvents, "overflow");
        var sources = Candidates.ToDictionary(item => item.EvidenceId, item => item.SourceRevision, StringComparer.Ordinal);
        if (TruncationEvents.Concat(OverflowEvents).Any(item => !sources.TryGetValue(item.EvidenceId, out var revision) || revision != item.SourceRevision))
            throw new ArgumentException("Observation events must refer to a candidate evidence ID and source revision.");
        if (CandidateFileCount != CountLocations(Candidates, item => item.FileLocationKey) ||
            SelectedFileCount != CountLocations(Selected, item => item.FileLocationKey) ||
            CandidateSymbolCount != CountLocations(Candidates, item => item.SymbolLocationKey) ||
            SelectedSymbolCount != CountLocations(Selected, item => item.SymbolLocationKey))
            throw new ArgumentException("Evidence efficiency location counts do not reconcile.");
        if (CandidateBytes != Candidates.Sum(item => item.Utf8Bytes!.Value) ||
            CandidateTokens != (Candidates.All(item => item.Tokens.HasValue) ? Candidates.Sum(item => item.Tokens!.Value) : null) ||
            SelectedBytes != Selected.Sum(item => item.Utf8Bytes!.Value) ||
            SelectedTokens != (Selected.All(item => item.Tokens.HasValue) ? Selected.Sum(item => item.Tokens!.Value) : null))
            throw new ArgumentException("Evidence efficiency totals do not reconcile.");
        if (Candidates.Concat(Selected).GroupBy(item => item.EvidenceId, StringComparer.Ordinal).Any(group => group.Select(item => item.SourceRevision).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new ArgumentException("An evidence ID cannot refer to multiple source revisions.");
        decimal? expectedRatio = CandidateTokens is null || SelectedTokens is null || CandidateTokens == 0
            ? null : (decimal)SelectedTokens.Value / CandidateTokens.Value;
        var expectedReason = CandidateTokens is null || SelectedTokens is null
            ? "token-measurement-unavailable" : CandidateTokens == 0 ? "candidate-tokens-zero" : null;
        if (SelectionRatio != expectedRatio || SelectionRatioUnavailableReason != expectedReason)
            throw new ArgumentException("Invalid selection ratio.");
        if (RoleInputBudgetTokens == 0 ? UtilizationPercent is not null || UtilizationUnavailableReason != "role-input-budget-zero" :
            UtilizationPercent != (RenderedInputTokens is null ? null : 100m * RenderedInputTokens.Value / RoleInputBudgetTokens) ||
            UtilizationUnavailableReason != (RenderedInputTokens is null ? "rendered-input-tokens-unavailable" : null))
            throw new ArgumentException("Invalid budget utilization.");
    }

    private static int CountLocations(IEnumerable<EvidenceEfficiencyItem> items, Func<EvidenceEfficiencyItem, string?> key) =>
        items.Select(key).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count();

    private static void ValidateEvents(IEnumerable<EvidenceEfficiencyEvent> events, string eventType)
    {
        if (events.Any(item => item is null || item.EventType != eventType || string.IsNullOrWhiteSpace(item.EvidenceId) ||
            string.IsNullOrWhiteSpace(item.SourceRevision) || string.IsNullOrWhiteSpace(item.Reason) ||
            item.OriginalBytes < 0 || item.ResultBytes < 0 || item.OriginalTokens < 0 || item.ResultTokens < 0))
            throw new ArgumentException("Invalid evidence efficiency observation event.");
    }
}

public interface IEvidenceEfficiencyMeasurer
{
    EvidenceEfficiencyMeasurement Measure(string? tokenizerId, IReadOnlyList<EvidenceEfficiencyItem> candidates,
        IReadOnlyList<string> selectedEvidenceIds, string role, long roleInputBudgetTokens,
        long? renderedInputTokens, IReadOnlyList<EvidenceEfficiencyEvent>? truncationEvents = null,
        IReadOnlyList<EvidenceEfficiencyEvent>? overflowEvents = null);
}
