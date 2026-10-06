namespace SdevEng;

/// <summary>Caller-supplied WorkUnit facts; framework versions are exact metadata versions.</summary>
public sealed record SkillFrameworkFact(string Id, string Version);

public sealed record SkillActivationContext(
    string Role, string[] RequestedCapabilities, string[] RequestedTools,
    string[] AvailableTools, SkillFrameworkFact[] Frameworks)
{
    public void Validate()
    {
        if (Role is not ("planner" or "coder" or "test-author" or "reviewer" or "repair"))
            throw new ArgumentException("Unknown skill activation role.");
        if (RequestedCapabilities is null || RequestedTools is null || AvailableTools is null || Frameworks is null ||
            RequestedCapabilities.Concat(RequestedTools).Concat(AvailableTools).Any(string.IsNullOrWhiteSpace) ||
            Frameworks.Any(fact => fact is null || string.IsNullOrWhiteSpace(fact.Id) ||
                !System.Text.RegularExpressions.Regex.IsMatch(fact.Version ?? "", "^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?$")) ||
            Frameworks.Select(fact => fact.Id).Distinct(StringComparer.Ordinal).Count() != Frameworks.Length)
            throw new ArgumentException("Invalid skill activation facts.");
    }
}
