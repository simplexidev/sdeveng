using System.Text.Json;
using SdevEng;

namespace SdevEng.Infrastructure;

public static class ContextBudgetPolicyReader
{
    public static ContextBudgetPolicy Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root)) throw new InvalidDataException("Context budget policy must be an object with unique fields.");
        var policy = new ContextBudgetPolicy
        {
            Version = RequiredInt(root, "version"),
            MaxContextTokens = RequiredInt(root, "maxContextTokens"),
            DefaultInputTokens = OptionalInt(root, "defaultInputTokens", 4096),
            ReservedOutputTokens = OptionalInt(root, "reservedOutputTokens", 1024),
            EvidenceShare = OptionalDouble(root, "evidenceShare", 0.5),
            SkillsShare = OptionalDouble(root, "skillsShare", 0.25),
            RoleInputTokens = ReadRoleOverrides(root)
        };
        if (root.TryGetProperty("overflowBehavior", out var overflow) && (overflow.ValueKind != JsonValueKind.String || overflow.GetString() != "drop-optional-evidence-then-references"))
            throw new InvalidDataException("Unknown overflow behavior.");
        var allowed = new HashSet<string>(["$schema", "version", "maxContextTokens", "defaultInputTokens", "reservedOutputTokens", "evidenceShare", "skillsShare", "roleInputTokens", "overflowBehavior"], StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new InvalidDataException("Context budget policy contains an unknown field.");
        _ = ContextBudgetEvaluator.Evaluate(policy, "planner");
        return policy;
    }

    static Dictionary<string, int> ReadRoleOverrides(JsonElement root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("roleInputTokens", out var roles)) return result;
        if (roles.ValueKind != JsonValueKind.Object) throw new InvalidDataException("roleInputTokens must be an object.");
        foreach (var role in roles.EnumerateObject())
        {
            if (role.Value.ValueKind != JsonValueKind.Number || !role.Value.TryGetInt32(out var count)) throw new InvalidDataException("Role input budgets must be integers.");
            if (!result.TryAdd(role.Name, count)) throw new InvalidDataException("Duplicate worker role budget.");
        }
        return result;
    }

    static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        return false;
    }

    static int RequiredInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number : throw new InvalidDataException($"{name} is required and must be an integer.");
    static int OptionalInt(JsonElement root, string name, int fallback) => !root.TryGetProperty(name, out var value) ? fallback : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new InvalidDataException($"{name} must be an integer.");
    static double OptionalDouble(JsonElement root, string name, double fallback) => !root.TryGetProperty(name, out var value) ? fallback : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : throw new InvalidDataException($"{name} must be a number.");
}

/// <summary>Loads canonical policy and returns its effective allowance in one operation.</summary>
public sealed class ContextBudgetService(string policyPath)
{
    public ContextBudgetExplanation Explain(string role, IReadOnlyList<ContextBudgetItem> items, int? modelContextTokens = null) =>
        ContextBudgetOverflowEvaluator.Explain(ContextBudgetPolicyReader.Read(policyPath), role, items, modelContextTokens);

    public ContextBudgetExplanation ExplainFile(string role, string measurementsPath, int? modelContextTokens = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(measurementsPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 1024) throw new InvalidDataException("Measurements must be an array of at most 1024 items.");
        var items = new List<ContextBudgetItem>();
        foreach (var item in root.EnumerateArray())
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => !fields.Add(p.Name)) ||
                !fields.SetEquals(["id", "kind", "tokens", "required", "priority"])) throw new InvalidDataException("Invalid measurement fields.");
            items.Add(new(item.GetProperty("id").GetString()!, item.GetProperty("kind").GetString()!,
                item.GetProperty("tokens").GetInt32(), item.GetProperty("required").GetBoolean(), item.GetProperty("priority").GetInt32()));
        }
        return Explain(role, items, modelContextTokens);
    }
    public EffectiveContextBudget ForRole(string role, int? modelContextTokens = null) =>
        ContextBudgetEvaluator.Evaluate(ContextBudgetPolicyReader.Read(policyPath), role, modelContextTokens);
}
