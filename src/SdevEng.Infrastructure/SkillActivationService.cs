namespace SdevEng;

/// <summary>Selects canonical skill metadata without loading instructions or references.</summary>
public sealed class SkillActivationService
{
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
