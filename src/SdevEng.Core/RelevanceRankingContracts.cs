using System.Text.Json.Serialization;

namespace SdevEng;

/// <summary>Bounded input for a semantic relevance ranking pass after deterministic filtering.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RelevanceRankingInput
{
    public const int CandidateLimit = 25;

    public string Query { get; init; } = "";
    public RelevanceRankingCandidate[] Candidates { get; init; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Query)) throw new ArgumentException("A ranking query is required.");
        if (Candidates.Length > CandidateLimit) throw new ArgumentException($"At most {CandidateLimit} ranking candidates are allowed.");
        if (Candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Text)))
            throw new ArgumentException("Every ranking candidate requires a non-empty id and text.");
        if (Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != Candidates.Length)
            throw new ArgumentException("Ranking candidate ids must be unique.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RelevanceRankingCandidate
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>Provides semantic relevance scores for an already bounded candidate set.</summary>
public interface IRelevanceRankingProvider
{
    Task<IReadOnlyList<RelevanceRankingScore>> RankAsync(RelevanceRankingInput input, CancellationToken cancellationToken = default);
}

/// <summary>Safe default that abstains from semantic ranking without external calls.</summary>
public sealed class AbstainingRelevanceRankingProvider : IRelevanceRankingProvider
{
    public Task<IReadOnlyList<RelevanceRankingScore>> RankAsync(RelevanceRankingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RelevanceRankingScore>>(Array.Empty<RelevanceRankingScore>());
    }
}

/// <summary>A provider's relevance score for one candidate in the supplied input.</summary>
public record RelevanceRankingScore(string CandidateId, double Score)
{
    /// <summary>Provider confidence, distinct from relevance. Missing confidence abstains.</summary>
    public double? Confidence { get; init; }
}

/// <summary>Policy for accepting provider scores when ordering optional candidates.</summary>
public sealed record RelevanceRankingPolicy
{
    public double MinConfidence { get; init; } = .80;

    public void Validate()
    {
        if (!double.IsFinite(MinConfidence) || MinConfidence is < 0 or > 1)
            throw new ArgumentException("Ranking minimum confidence must be finite and within [0,1].");
    }
}

/// <summary>Applies accepted semantic scores without changing the candidate set.</summary>
public static class RelevanceRankingOrder
{
    public static IReadOnlyList<RelevanceRankingCandidate> Apply(
        RelevanceRankingInput input,
        IReadOnlyList<RelevanceRankingScore>? scores,
        RelevanceRankingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        policy ??= new RelevanceRankingPolicy();
        policy.Validate();

        if (scores is null || scores.Count == 0) return input.Candidates.ToArray();

        var counts = scores.Where(score => score is not null)
            .GroupBy(score => score.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var accepted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var score in scores)
        {
            if (score is null || string.IsNullOrWhiteSpace(score.CandidateId) ||
                !counts.TryGetValue(score.CandidateId, out var count) || count != 1 ||
                !double.IsFinite(score.Score) || score.Score is < 0 or > 1 ||
                score.Confidence is not double confidence || !double.IsFinite(confidence) ||
                confidence is < 0 or > 1 || confidence < policy.MinConfidence ||
                !input.Candidates.Any(candidate => candidate.Id == score.CandidateId))
                continue;
            accepted[score.CandidateId] = score.Score;
        }

        return input.Candidates
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(item => accepted.ContainsKey(item.candidate.Id))
            .ThenByDescending(item => accepted.TryGetValue(item.candidate.Id, out var relevance) ? relevance : 0)
            .ThenBy(item => accepted.ContainsKey(item.candidate.Id) ? item.candidate.Id : "", StringComparer.Ordinal)
            .ThenBy(item => item.index)
            .Select(item => item.candidate)
            .ToArray();
    }
}
