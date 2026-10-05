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

public sealed record EffectiveContextBudget(
    string Role,
    int InputTokens,
    int ReservedOutputTokens,
    int ModelContextTokens,
    int EvidenceTokens,
    int SkillsTokens);

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
        return new(
            role,
            input,
            policy.ReservedOutputTokens,
            context,
            (int)Math.Floor(input * policy.EvidenceShare),
            (int)Math.Floor(input * policy.SkillsShare));
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

/// <summary>Synthetic measurements; lower priority numbers are retained first.</summary>
public sealed record ContextBudgetItem(string Id, string Kind, int Tokens, bool Required, int Priority);
public sealed record ContextBudgetDrop(string Id, string Reason);
public sealed record ContextBudgetExplanation(
    string Kind, int SchemaVersion, string Status, EffectiveContextBudget Budget,
    string MeasurementKind, long ConsumedTokens, long RemainingTokens,
    long EvidenceConsumedTokens, long SkillsConsumedTokens,
    IReadOnlyList<ContextBudgetDrop> Dropped, bool MandatoryOverflow);

public static class ContextBudgetOverflowEvaluator
{
    public static ContextBudgetExplanation Explain(ContextBudgetPolicy policy, string role,
        IReadOnlyList<ContextBudgetItem> items, int? modelContextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (modelContextTokens is < 1) throw new ArgumentException("Model context must be positive.", nameof(modelContextTokens));
        if (items.Count > 1024) throw new ArgumentException("At most 1024 measured items are supported.", nameof(items));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 || !ids.Add(item.Id) ||
                item.Kind is not ("mandatory" or "evidence" or "reference" or "skills") || item.Tokens < 0 || item.Priority < 0 ||
                (!item.Required && item.Kind is "mandatory" or "skills"))
                throw new ArgumentException("Invalid or duplicate measured item; mandatory and skills items must be required.", nameof(items));
        EffectiveContextBudget budget;
        var contextOverflow = false;
        try { budget = ContextBudgetEvaluator.Evaluate(policy, role, modelContextTokens); }
        catch (ContextBudgetExceededException)
        {
            var input = policy.RoleInputTokens.TryGetValue(role, out var configured) ? configured : policy.DefaultInputTokens;
            budget = new(role, input, policy.ReservedOutputTokens, modelContextTokens ?? policy.MaxContextTokens,
                (int)Math.Floor(input * policy.EvidenceShare), (int)Math.Floor(input * policy.SkillsShare));
            contextOverflow = true;
        }
        var kept = items.ToList();
        var dropped = new List<ContextBudgetDrop>();
        long Count(string? kind = null) => kept.Where(i => kind is null || i.Kind == kind).Sum(i => (long)i.Tokens);
        bool Fits() => Count() <= budget.InputTokens && Count("evidence") <= budget.EvidenceTokens && Count("skills") <= budget.SkillsTokens;
        foreach (var kind in new[] { "evidence", "reference" })
            foreach (var item in items.Select((item, index) => (item, index))
                .Where(x => !x.item.Required && x.item.Kind == kind)
                .OrderByDescending(x => x.item.Priority).ThenByDescending(x => x.index).Select(x => x.item))
            {
                if (Fits()) break;
                var reason = Count("evidence") > budget.EvidenceTokens ? "evidence-share-limit" :
                    Count("skills") > budget.SkillsTokens ? "skills-share-limit" : "input-limit";
                kept.Remove(item);
                dropped.Add(new(item.Id, reason));
            }
        var overflow = contextOverflow || !Fits();
        return new("context-budget-explain", 1, overflow ? "budget-exceeded" : "ok", budget, "synthetic",
            Count(), Math.Max(0, budget.InputTokens - Count()), Count("evidence"), Count("skills"), dropped, overflow);
    }
}
