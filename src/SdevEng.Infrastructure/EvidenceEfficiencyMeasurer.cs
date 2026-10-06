using System.Text;
using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Counts evidence candidates and selections with the same registered exact tokenizer.</summary>
public sealed class EvidenceEfficiencyMeasurer(TokenizerRegistry tokenizers) : IEvidenceEfficiencyMeasurer
{
    public EvidenceEfficiencyMeasurement Measure(string? tokenizerId, IReadOnlyList<EvidenceEfficiencyItem> candidates,
        IReadOnlyList<string> selectedEvidenceIds, string role, long roleInputBudgetTokens,
        long? renderedInputTokens)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(selectedEvidenceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (roleInputBudgetTokens < 0 || renderedInputTokens < 0) throw new ArgumentOutOfRangeException(nameof(roleInputBudgetTokens));
        var tokenizer = string.IsNullOrWhiteSpace(tokenizerId) ? null : tokenizers.Resolve(tokenizerId);
        var measured = new Dictionary<string, EvidenceEfficiencyItem>(StringComparer.Ordinal);
        foreach (var item in candidates)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.EvidenceId) || string.IsNullOrWhiteSpace(item.SourceRevision) || item.Text is null)
                throw new ArgumentException("Each candidate requires an evidence ID, source revision, and text.", nameof(candidates));
            var bytes = Encoding.UTF8.GetByteCount(item.Text);
            if (item.Utf8Bytes is not null && item.Utf8Bytes != bytes) throw new ArgumentException("Candidate byte measurement does not match its text.", nameof(candidates));
            var tokens = item.Tokens ?? (tokenizer is null ? null : tokenizer.CountTokens(item.Text));
            if (measured.TryGetValue(item.EvidenceId, out var prior))
            {
                if (prior.SourceRevision != item.SourceRevision || prior.Text != item.Text || prior.Utf8Bytes != bytes ||
                    item.Tokens is not null && prior.Tokens != item.Tokens || tokens is not null && prior.Tokens != tokens)
                    throw new ArgumentException("Duplicate evidence IDs must have identical revision, text, and measurements.", nameof(candidates));
                continue;
            }
            measured.Add(item.EvidenceId, item with { Utf8Bytes = bytes, Tokens = tokens });
        }
        if (selectedEvidenceIds.Any(string.IsNullOrWhiteSpace) || selectedEvidenceIds.Distinct(StringComparer.Ordinal).Count() != selectedEvidenceIds.Count ||
            selectedEvidenceIds.Any(id => !measured.ContainsKey(id)))
            throw new ArgumentException("Selections must refer to unique candidate evidence IDs.", nameof(selectedEvidenceIds));
        var candidateArray = measured.Values.ToArray();
        var selected = selectedEvidenceIds.Select(id => measured[id]).ToArray();
        var candidateBytes = candidateArray.Sum(item => item.Utf8Bytes!.Value);
        var candidateTokens = candidateArray.All(item => item.Tokens.HasValue) ? candidateArray.Sum(item => item.Tokens!.Value) : (long?)null;
        var selectedBytes = selected.Sum(item => item.Utf8Bytes!.Value);
        var selectedTokens = selected.All(item => item.Tokens.HasValue) ? selected.Sum(item => item.Tokens!.Value) : (long?)null;
        decimal? selectionRatio = candidateTokens is null || selectedTokens is null || candidateTokens == 0
            ? null : (decimal)selectedTokens.Value / candidateTokens.Value;
        var selectionRatioUnavailableReason = candidateTokens is null || selectedTokens is null
            ? "token-measurement-unavailable" : candidateTokens == 0 ? "candidate-tokens-zero" : null;
        var result = new EvidenceEfficiencyMeasurement(EvidenceEfficiencyMeasurement.CurrentSchemaVersion,
            EvidenceEfficiencyMeasurement.ResultKind, candidateArray, selected, candidateBytes, candidateTokens,
            selectedBytes, selectedTokens, selectionRatio, selectionRatioUnavailableReason,
            role, roleInputBudgetTokens, renderedInputTokens,
            roleInputBudgetTokens == 0 || renderedInputTokens is null ? null : 100m * renderedInputTokens.Value / roleInputBudgetTokens,
            roleInputBudgetTokens == 0 ? "role-input-budget-zero" : renderedInputTokens is null ? "rendered-input-tokens-unavailable" : null);
        result.Validate();
        return result;
    }
}
