using SdevEng;
using SdevEng.Infrastructure;

namespace SdevEng.Tests;

public sealed class ContextBudgetTests
{
    [Fact]
    public void ServiceLoadsPolicyAndUsesRoleOverrideAndReservedOutput()
    {
        var path = Path.Combine(AgentTool.FindToolkit(), "config", "context-budget-policy.json");
        var service = new ContextBudgetService(path);
        var standard = service.ForRole("planner");
        Assert.Equal(4096, standard.InputTokens);
        Assert.Equal(1024, standard.ReservedOutputTokens);
        Assert.Equal(32000, standard.ModelContextTokens);

        var json = File.ReadAllText(path).Replace("\"roleInputTokens\": {}", "\"roleInputTokens\": { \"coder\": 8192 }");
        var temp = Path.Combine(Path.GetTempPath(), $"context-budget-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temp, json);
            Assert.Equal(8192, new ContextBudgetService(temp).ForRole("coder").InputTokens);
            Assert.Throws<ContextBudgetExceededException>(() => new ContextBudgetService(temp).ForRole("coder", 8192));
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void EvaluatorRejectsUnknownRolesAndInvalidPolicy()
    {
        var policy = new ContextBudgetPolicy { MaxContextTokens = 10000 };
        Assert.Throws<ArgumentException>(() => ContextBudgetEvaluator.Evaluate(policy, "worker"));
        Assert.Throws<ArgumentException>(() => ContextBudgetEvaluator.Evaluate(policy with { ReservedOutputTokens = -1 }, "planner"));
        Assert.Throws<ContextBudgetExceededException>(() => ContextBudgetEvaluator.Evaluate(policy with { RoleInputTokens = new() { ["planner"] = 10001 } }, "planner"));
    }

    [Theory]
    [InlineData(0.50, 0.25)]
    [InlineData(0.75, 0.25)]
    [InlineData(1.00, 0.00)]
    [InlineData(0.00, 1.00)]
    public void ServiceAcceptsValidShareBoundaries(double evidence, double skills)
    {
        var policy = $"{{\"version\":1,\"maxContextTokens\":32000,\"evidenceShare\":{evidence},\"skillsShare\":{skills}}}";
        WithPolicy(policy, path =>
        {
            var loaded = ContextBudgetPolicyReader.Read(path);
            Assert.Equal(evidence, loaded.EvidenceShare);
            Assert.Equal(skills, loaded.SkillsShare);
            Assert.Equal(4096, new ContextBudgetService(path).ForRole("planner").InputTokens);
        });
    }

    [Fact]
    public void ServiceRejectsCombinedShareAboveOneAndEvaluatorCannotBeBypassed()
    {
        WithPolicy("{\"version\":1,\"maxContextTokens\":32000,\"evidenceShare\":0.75,\"skillsShare\":0.50}",
            path => Assert.Throws<ArgumentException>(() => new ContextBudgetService(path).ForRole("planner")));
        Assert.Throws<ArgumentException>(() => ContextBudgetEvaluator.Evaluate(
            new ContextBudgetPolicy { MaxContextTokens = 32000, EvidenceShare = 0.75, SkillsShare = 0.50 }, "planner"));
    }

    [Fact]
    public void ReaderUsesOptionalDefaultsAndAcceptsMinimalVersionOneFixture()
    {
        WithPolicy("{\"version\":1,\"maxContextTokens\":32000}", path =>
        {
            var budget = new ContextBudgetService(path).ForRole("planner");
            Assert.Equal(4096, budget.InputTokens);
            Assert.Equal(1024, budget.ReservedOutputTokens);
        });
        WithPolicy("{\"version\":1,\"maxContextTokens\":32000,\"reservedOutputTokens\":4000}", path =>
            Assert.Equal(4000, ContextBudgetPolicyReader.Read(path).ReservedOutputTokens));
    }

    static void WithPolicy(string json, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-budget-{Guid.NewGuid():N}.json");
        try { File.WriteAllText(path, json); action(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReaderRejectsUnknownAndDuplicatePolicyIdentities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-budget-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"maxContextTokens\":10000,\"roleInputTokens\":{\"alien\":4000}}");
            Assert.Throws<ArgumentException>(() => ContextBudgetPolicyReader.Read(path));
            File.WriteAllText(path, "{\"version\":1,\"version\":1,\"maxContextTokens\":10000}");
            Assert.Throws<InvalidDataException>(() => ContextBudgetPolicyReader.Read(path));
        }
        finally { File.Delete(path); }
    }
}
