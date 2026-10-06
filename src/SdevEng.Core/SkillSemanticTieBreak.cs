namespace SdevEng;

/// <summary>Optional ranking seam for already eligible, equal-priority skill candidates.</summary>
public interface ISkillSemanticTieBreakProvider : IRelevanceRankingProvider { }

/// <summary>Default skill tie-break abstains without transport or content loading.</summary>
public sealed class AbstainingSkillSemanticTieBreakProvider : ISkillSemanticTieBreakProvider
{
    private readonly AbstainingRelevanceRankingProvider provider = new();

    public Task<IReadOnlyList<RelevanceRankingScore>> RankAsync(
        RelevanceRankingInput input, CancellationToken cancellationToken = default) =>
        provider.RankAsync(input, cancellationToken);
}
