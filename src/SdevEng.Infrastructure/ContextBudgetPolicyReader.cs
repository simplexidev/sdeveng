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
        var allowed = new HashSet<string>(["$schema", "version", "maxContextTokens", "defaultInputTokens", "reservedOutputTokens", "evidenceShare", "skillsShare", "roleInputTokens"], StringComparer.Ordinal);
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

    static int RequiredInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
        ? number : throw new InvalidDataException($"{name} is required and must be an integer.");
    static int OptionalInt(JsonElement root, string name, int fallback) => !root.TryGetProperty(name, out var value) ? fallback : value.TryGetInt32(out var number) ? number : throw new InvalidDataException($"{name} must be an integer.");
    static double OptionalDouble(JsonElement root, string name, double fallback) => !root.TryGetProperty(name, out var value) ? fallback : value.TryGetDouble(out var number) ? number : throw new InvalidDataException($"{name} must be a number.");
}

/// <summary>Loads canonical policy and returns its effective allowance in one operation.</summary>
public sealed class ContextBudgetService(string policyPath)
{
    public EffectiveContextBudget ForRole(string role, int? modelContextTokens = null) =>
        ContextBudgetEvaluator.Evaluate(ContextBudgetPolicyReader.Read(policyPath), role, modelContextTokens);
}
