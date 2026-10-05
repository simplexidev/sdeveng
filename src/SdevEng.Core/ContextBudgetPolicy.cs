namespace SdevEng;

/// <summary>Token limits that bound rendered worker input independently of model context.</summary>
public sealed record ContextBudgetPolicy
{
    public int Version { get; init; } = 1;
    public int MaxContextTokens { get; init; }
    public int DefaultInputTokens { get; init; } = 4096;
    public int ReservedOutputTokens { get; init; } = 1024;
    public double EvidenceShare { get; init; } = 0.5;
    public double SkillsShare { get; init; } = 0.25;
    public Dictionary<string, int> RoleInputTokens { get; init; } = new(StringComparer.Ordinal);
}

public sealed record EffectiveContextBudget(string Role, int InputTokens, int ReservedOutputTokens, int ModelContextTokens);

/// <summary>Validates a role's effective input/output allowances against the selected model context.</summary>
public static class ContextBudgetEvaluator
{
    static readonly HashSet<string> Roles = new(["planner", "coder", "test-author", "reviewer", "repair"], StringComparer.Ordinal);

    public static EffectiveContextBudget Evaluate(ContextBudgetPolicy policy, string role, int? modelContextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Version != 1 || policy.MaxContextTokens < 1 || policy.DefaultInputTokens < 1 || policy.ReservedOutputTokens < 0 ||
            !double.IsFinite(policy.EvidenceShare) || !double.IsFinite(policy.SkillsShare) ||
            policy.EvidenceShare is < 0 or > 1 || policy.SkillsShare is < 0 or > 1 || policy.EvidenceShare + policy.SkillsShare > 1)
            throw new ArgumentException("Context budget policy is invalid.", nameof(policy));
        if (!Roles.Contains(role)) throw new ArgumentException("Unknown worker role.", nameof(role));
        foreach (var overrideEntry in policy.RoleInputTokens)
            if (!Roles.Contains(overrideEntry.Key) || overrideEntry.Value < 1) throw new ArgumentException("Role input budget override is invalid.", nameof(policy));

        var input = policy.RoleInputTokens.TryGetValue(role, out var configured) ? configured : policy.DefaultInputTokens;
        var context = modelContextTokens ?? policy.MaxContextTokens;
        if (context < 1 || input + (long)policy.ReservedOutputTokens > context)
            throw new ContextBudgetExceededException(role, input, policy.ReservedOutputTokens, context);
        return new(role, input, policy.ReservedOutputTokens, context);
    }
}

public sealed class ContextBudgetExceededException(string role, int inputTokens, int reservedOutputTokens, int modelContextTokens)
    : InvalidOperationException($"The {role} input and reserved output budget exceed the selected model context ({inputTokens}+{reservedOutputTokens}>{modelContextTokens}).")
{
    public string Role { get; } = role;
    public int InputTokens { get; } = inputTokens;
    public int ReservedOutputTokens { get; } = reservedOutputTokens;
    public int ModelContextTokens { get; } = modelContextTokens;
}
