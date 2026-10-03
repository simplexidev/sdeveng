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
public record RelevanceRankingScore(string CandidateId, double Score);
