namespace SdevEng;

/// <summary>A candidate or selected evidence item with its stable source revision and exact supplied measurement.</summary>
public sealed record EvidenceEfficiencyItem(string EvidenceId, string SourceRevision, string Text,
    long? Utf8Bytes, long? Tokens);

/// <summary>Versioned counts for the evidence candidates and selections supplied for one role.</summary>
public sealed record EvidenceEfficiencyMeasurement(int SchemaVersion, string Kind,
    IReadOnlyList<EvidenceEfficiencyItem> Candidates, IReadOnlyList<EvidenceEfficiencyItem> Selected,
    long CandidateBytes, long? CandidateTokens, long SelectedBytes, long? SelectedTokens,
    decimal? SelectionRatio, string? SelectionRatioUnavailableReason,
    string Role, long RoleInputBudgetTokens, long? RenderedInputTokens,
    decimal? UtilizationPercent, string? UtilizationUnavailableReason)
{
    public const int CurrentSchemaVersion = 1;
    public const string ResultKind = "evidence-efficiency-measurement";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Kind != ResultKind || Candidates is null || Selected is null ||
            string.IsNullOrWhiteSpace(Role) || RoleInputBudgetTokens < 0 || RenderedInputTokens < 0 ||
            Candidates.Concat(Selected).Any(item => item is null || string.IsNullOrWhiteSpace(item.EvidenceId) ||
                string.IsNullOrWhiteSpace(item.SourceRevision) || item.Text is null || item.Utf8Bytes < 0 || item.Tokens < 0))
            throw new ArgumentException("Invalid evidence efficiency measurement.");
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
}

public interface IEvidenceEfficiencyMeasurer
{
    EvidenceEfficiencyMeasurement Measure(string? tokenizerId, IReadOnlyList<EvidenceEfficiencyItem> candidates,
        IReadOnlyList<string> selectedEvidenceIds, string role, long roleInputBudgetTokens,
        long? renderedInputTokens);
}
