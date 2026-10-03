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
