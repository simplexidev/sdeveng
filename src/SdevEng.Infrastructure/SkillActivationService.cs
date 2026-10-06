namespace SdevEng;

/// <summary>Selects canonical skill metadata without loading instructions or references.</summary>
public sealed class SkillActivationService(
    ISkillSemanticTieBreakProvider? tieBreakProvider = null,
    RelevanceRankingPolicy? rankingPolicy = null)
{
    private readonly ISkillSemanticTieBreakProvider tieBreak = tieBreakProvider ?? new AbstainingSkillSemanticTieBreakProvider();

    /// <summary>Optional bounded semantic ordering; eligibility and deterministic priority remain authoritative.</summary>
    public async Task<IReadOnlyList<SkillMetadata>> ActivateAsync(
        string toolkitRoot, SkillActivationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = rankingPolicy ?? new RelevanceRankingPolicy();
        policy.Validate();
        var eligible = Activate(toolkitRoot, context);
        var result = new List<SkillMetadata>();
        foreach (var group in eligible.GroupBy(skill => Priority(skill, context)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skills = group.ToArray();
            if (skills.Length is < 2 or > RelevanceRankingInput.CandidateLimit)
            {
                result.AddRange(skills);
                continue;
            }
            var input = new RelevanceRankingInput
            {
                Query = $"Skill activation for role {context.Role}",
                Candidates = skills.Select(skill => new RelevanceRankingCandidate { Id = skill.Id!, Text = skill.Id! }).ToArray()
            };
            input.Validate();
            var scores = await tieBreak.RankAsync(input, cancellationToken).ConfigureAwait(false);
            var byId = skills.ToDictionary(skill => skill.Id!, StringComparer.Ordinal);
            result.AddRange(RelevanceRankingOrder.Apply(input, scores, policy).Select(candidate => byId[candidate.Id]));
        }
        return result;
    }

    public IReadOnlyList<SkillMetadata> Activate(string toolkitRoot, SkillActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        return SkillCompatibilityMapReader.ReadMetadataIndex(toolkitRoot)
            .Where(skill => skill.SupportedRoles is null || skill.SupportedRoles.Contains(context.Role, StringComparer.Ordinal))
            .Where(skill => (skill.RequiredTools ?? []).All(tool => context.AvailableTools.Contains(tool, StringComparer.Ordinal)))
            .Select(skill => (Skill: skill, Priority: Priority(skill, context)))
            .Where(candidate => candidate.Priority > 0)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Skill.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Skill).ToArray();
    }

    private static int Priority(SkillMetadata skill, SkillActivationContext context)
    {
        var priority = 0;
        foreach (var condition in skill.Activation ?? [])
        {
            var framework = context.Frameworks.SingleOrDefault(fact => fact.Id == condition.Id);
            if (condition.FrameworkVersion is not null)
            {
                if (framework?.Version == condition.FrameworkVersion) priority = Math.Max(priority, 2);
            }
            else if (framework is not null || context.RequestedCapabilities.Contains(condition.Id, StringComparer.Ordinal) ||
                     (context.RequestedTools.Contains(condition.Id, StringComparer.Ordinal) &&
                      context.AvailableTools.Contains(condition.Id, StringComparer.Ordinal)))
                priority = Math.Max(priority, 1);
        }
        return priority;
    }
}
