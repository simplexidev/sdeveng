using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng.Infrastructure;

namespace SdevEng.Tests;

public sealed class ContextBudgetExplainTests
{
    static string Toolkit => AgentTool.FindToolkit();
    static JsonSchema Schema(string name) => JsonSchema.FromFile(Path.Combine(Toolkit, "schemas", name + ".schema.json"));

    [Fact]
    public void EvaluationFixtureProvesNormalServiceTrimmingAndRejection()
    {
        Assert.True(Schema("context-budget-policy").Evaluate(JsonNode.Parse(File.ReadAllText(Path.Combine(Toolkit, "config/context-budget-policy.json")))!).IsValid);
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Toolkit, "evals/context-budget/fixtures/scenario.json")))!;
        var items = fixture["items"]!.Deserialize<ContextBudgetItem[]>(AgentTool.Json)!;
        Assert.True(Schema("context-budget-measurements").Evaluate(fixture["items"]!).IsValid);
        var result = Service().Explain(fixture["role"]!.GetValue<string>(), items);
        Assert.Equal(fixture["expectedStatus"]!.GetValue<string>(), result.Status);
        Assert.Equal(fixture["expectedDropped"]!.AsArray().Select(n => n!.GetValue<string>()), result.Dropped.Select(d => d.Id));
        Assert.True(Service().Explain("planner", [items[0] with { Tokens = fixture["mandatoryOverflowTokens"]!.GetValue<int>() }]).MandatoryOverflow);
        Assert.Equal(0, Evaluation.Run(Toolkit, "context-budget", null).ExitCode);
    }

    [Fact]
    public void StableTrimmingPreservesMandatoryFactsAndEnforcesShares()
    {
        var policy = new ContextBudgetPolicy { MaxContextTokens = 100, DefaultInputTokens = 10, ReservedOutputTokens = 2 };
        ContextBudgetItem[] items = [new("safety", "mandatory", 6, true, 0), new("e1", "evidence", 2, false, 1),
            new("e2", "evidence", 2, false, 1), new("e3", "evidence", 2, false, 2), new("r", "reference", 3, false, 99)];
        var result = ContextBudgetOverflowEvaluator.Explain(policy, "coder", items);
        Assert.Equal("ok", result.Status);
        Assert.Equal(new[] { "e3", "e2", "e1" }, result.Dropped.Select(d => d.Id));
        Assert.Equal(new[] { "evidence-share-limit", "input-limit", "input-limit" }, result.Dropped.Select(d => d.Reason));
        Assert.Equal(9, result.ConsumedTokens);
        Assert.Equal(1, result.RemainingTokens);
        var references = ContextBudgetOverflowEvaluator.Explain(policy, "coder", [new("facts", "mandatory", 10, true, 0), new("e", "evidence", 1, false, 0), new("r", "reference", 1, false, 0)]);
        Assert.Equal(new[] { "e", "r" }, references.Dropped.Select(d => d.Id));
        Assert.True(Schema("context-budget-explain").Evaluate(JsonSerializer.SerializeToNode(result, AgentTool.Json)!).IsValid);
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("coder")]
    [InlineData("test-author")]
    [InlineData("reviewer")]
    [InlineData("repair")]
    public async Task NormalDispatchBindsPolicyAndProducesSchemaValidExplain(string role)
    {
        var result = await CommandTestRuntime.Execute(Cli.Parse(["config", "explain", "--role", role]), Toolkit, Toolkit, Settings.Load(Toolkit));
        var node = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
        Assert.Equal("ok", node["status"]!.GetValue<string>());
        Assert.Equal(4096, node["budget"]!["inputTokens"]!.GetValue<int>());
        Assert.Equal(role, node["budget"]!["role"]!.GetValue<string>());
        Assert.True(Schema("context-budget-explain").Evaluate(node).IsValid);
    }

    [Fact]
    public async Task NormalDispatchRejectsMandatoryOverflowWithoutTruncation()
    {
        const string measurements = """[{"id":"safety-output-facts","kind":"mandatory","tokens":5000,"required":true,"priority":0}]""";
        Assert.True(Schema("context-budget-measurements").Evaluate(JsonNode.Parse(measurements)!).IsValid);
        var path = Path.Combine(Path.GetTempPath(), $"measurements-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, measurements);
            var result = await CommandTestRuntime.Execute(Cli.Parse(["config", "explain", "--role", "repair", "--measurements", path]), Toolkit, Toolkit, Settings.Load(Toolkit));
            var node = JsonSerializer.SerializeToNode(result.Data, AgentTool.Json)!;
            Assert.Equal("budget-exceeded", node["status"]!.GetValue<string>());
            Assert.True(node["mandatoryOverflow"]!.GetValue<bool>());
            Assert.Equal(5000, node["consumedTokens"]!.GetValue<int>());
            Assert.Empty(node["dropped"]!.AsArray());
            Assert.True(Schema("context-budget-explain").Evaluate(node).IsValid);
            var context = new ContextBudgetService(Path.Combine(Toolkit, "config", "context-budget-policy.json")).Explain("repair", [], 4096);
            Assert.Equal("budget-exceeded", context.Status);
            Assert.True(context.MandatoryOverflow);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("mandatory", 4097)]
    [InlineData("evidence", 2049)]
    [InlineData("skills", 1025)]
    public void RequiredLimitsReject(string kind, int tokens)
    {
        var result = new ContextBudgetService(Path.Combine(Toolkit, "config", "context-budget-policy.json"))
            .Explain("planner", [new("required", kind, tokens, true, 0)]);
        Assert.True(result.MandatoryOverflow);
        Assert.Equal(tokens, result.ConsumedTokens);
        Assert.Empty(result.Dropped);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"x\"}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"alien\",\"tokens\":0,\"required\":true,\"priority\":0}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"mandatory\",\"tokens\":-1,\"required\":true,\"priority\":0}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"mandatory\",\"tokens\":0,\"required\":false,\"priority\":0}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"reference\",\"tokens\":0.5,\"required\":false,\"priority\":0}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"reference\",\"tokens\":0,\"required\":\"false\",\"priority\":0}]")]
    [InlineData("[{\"id\":\"x\",\"kind\":\"reference\",\"tokens\":0,\"required\":false,\"priority\":-1}]")]
    public void MalformedMeasurementsFailSchemaAndReader(string json)
    {
        Assert.False(Schema("context-budget-measurements").Evaluate(JsonNode.Parse(json)!).IsValid);
        WithFile(json, path => Assert.ThrowsAny<Exception>(() => Service().ExplainFile("planner", path)));
    }

    [Fact]
    public void DuplicateIdentitiesFailAndMinimalKindsValidate()
    {
        foreach (var kind in new[] { "mandatory", "evidence", "reference", "skills" })
        {
            var item = $$"""{"id":"x","kind":"{{kind}}","tokens":0,"required":true,"priority":0}""";
            Assert.True(Schema("context-budget-measurements").Evaluate(JsonNode.Parse("[" + item + "]")!).IsValid);
            WithFile("[" + item + "]", path => Assert.Equal("ok", Service().ExplainFile("planner", path).Status));
            WithFile("[" + item + "," + item + "]", path => Assert.Throws<ArgumentException>(() => Service().ExplainFile("planner", path)));
            WithFile("[" + item.Replace("\"id\":\"x\"", "\"id\":\"x\",\"id\":\"y\"") + "]", path => Assert.Throws<InvalidDataException>(() => Service().ExplainFile("planner", path)));
        }
    }

    [Fact]
    public void ExplainContractRejectsMissingFieldsUnknownIdentitiesAndInvalidRanges()
    {
        var schema = Schema("context-budget-explain");
        var node = JsonSerializer.SerializeToNode(Service().Explain("planner", []), AgentTool.Json)!.AsObject();
        foreach (var field in node.Select(p => p.Key).ToArray())
        {
            var malformed = node.DeepClone().AsObject();
            malformed.Remove(field);
            Assert.False(schema.Evaluate(malformed).IsValid);
        }
        foreach (var field in node["budget"]!.AsObject().Select(p => p.Key).ToArray())
        {
            var malformed = node.DeepClone();
            malformed["budget"]!.AsObject().Remove(field);
            Assert.False(schema.Evaluate(malformed).IsValid);
        }
        node["schemaVersion"] = 2;
        Assert.False(schema.Evaluate(node).IsValid);
        node["schemaVersion"] = 1;
        node["budget"]!["role"] = "unknown";
        Assert.False(schema.Evaluate(node).IsValid);
        node["budget"]!["role"] = "planner";
        node["consumedTokens"] = -1;
        Assert.False(schema.Evaluate(node).IsValid);
        node["consumedTokens"] = 0;
        node["measurementKind"] = "tokenizer";
        Assert.False(schema.Evaluate(node).IsValid);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"maxContextTokens\":32000}")]
    [InlineData("{\"version\":1,\"maxContextTokens\":\"32000\"}")]
    [InlineData("{\"version\":1,\"maxContextTokens\":32000,\"overflowBehavior\":\"truncate\"}")]
    [InlineData("{\"version\":1,\"maxContextTokens\":32000,\"roleInputTokens\":{\"coder\":1,\"coder\":2}}")]
    public void PolicyRejectsMalformedFieldsAndVersion(string json) =>
        WithFile(json, path => Assert.ThrowsAny<Exception>(() => ContextBudgetPolicyReader.Read(path)));

    static ContextBudgetService Service() => new(Path.Combine(Toolkit, "config", "context-budget-policy.json"));
    static void WithFile(string json, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"budget-{Guid.NewGuid():N}.json");
        try { File.WriteAllText(path, json); action(path); }
        finally { File.Delete(path); }
    }
}
