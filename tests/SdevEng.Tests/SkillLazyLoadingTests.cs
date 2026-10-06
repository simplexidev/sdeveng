using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class SkillLazyLoadingTests
{
    [Fact]
    public async Task ActivationBoundaryLoadsOnlyRequestedBodyOrDeclaredReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "plugins/sdeveng"));
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/plugin.json"), "{\"version\":\"3.0.0\"}");
            var text = "Only this reference é. See missing.md";
            var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            var resources = JsonSerializer.Serialize(new[] {
                new SkillResource("one.md", "reference", hash),
                new SkillResource("missing.md", "reference", hash),
                new SkillResource("../outside.md", "reference", hash),
                new SkillResource("linked.md", "reference", hash),
                new SkillResource("script.md", "script", hash)
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var entries = new[] { "a-match", "z-match", "ineligible" }.Select(id =>
            {
                var path = $"plugins/sdeveng/skills/{id}/SKILL.md";
                var directory = Path.GetDirectoryName(Path.Combine(root, path))!;
                Directory.CreateDirectory(Path.Combine(directory, "agents"));
                File.WriteAllText(Path.Combine(directory, "agents/openai.yaml"), "interface: {}");
                File.WriteAllText(Path.Combine(root, path), $"---\nname: {id}\nactivation: '[{{\"id\":\"build\"}}]'\n" +
                    (id == "ineligible" ? "supportedRoles: '[\"reviewer\"]'\n" : "") +
                    $"resources: '{resources}'\n---\nInstructions for {id} é.");
                File.WriteAllText(Path.Combine(directory, "one.md"), text);
                return new SkillCompatibilityEntry(id, path, id, path, "3.0.0", [], "retained");
            }).ToArray();
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(
                new SkillCompatibilityMap("../schemas/skill-compatibility-map.schema.json", 1, entries),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var service = new SkillActivationService();
            var context = new SkillActivationContext("coder", ["build"], [], [], []);
            Assert.Equal(new[] { "a-match", "z-match" }, (await service.ActivateAsync(root, context)).Select(item => item.Id));
            var results = new List<SkillLoadResult>();
            var inactiveBody = await service.LoadInstructionsAsync(root, context, "ineligible");
            results.Add(inactiveBody);
            Assert.Equal("skill-not-activated", inactiveBody.OmissionReason);
            Assert.Null(inactiveBody.Content);
            var body = await service.LoadInstructionsAsync(root, context, "z-match");
            results.Add(body);
            Assert.Equal("Instructions for z-match é.", body.Content);
            Assert.Equal(Encoding.UTF8.GetByteCount(body.Content!), body.Utf8Bytes);
            Assert.Equal(body.Content!.Length, body.Characters);
            var reference = await service.LoadReferenceAsync(root, context, "z-match", "one.md");
            results.Add(reference);
            Assert.Equal(text, reference.Content);
            // Fixture evaluation of the current catalog -> activation -> lazy load -> role budget boundary.
            var vocabulary = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            File.WriteAllBytes(Path.Combine(root, "tokens.fixture"), vocabulary);
            var tokenizers = new SdevEng.Infrastructure.TokenizerRegistry(root);
            tokenizers.Register(new(1, "fixture", "fixture-model", "fixture", "r1",
                [new("tokens.fixture", Convert.ToHexString(SHA256.HashData(vocabulary)).ToLowerInvariant())],
                "fixture-byte-v1", [], "fixture-v1", true));
            var bodyTokens = body.Utf8Bytes;
            var policy = new ContextBudgetPolicy
            {
                MaxContextTokens = 1000,
                DefaultInputTokens = 1,
                ReservedOutputTokens = 0,
                EvidenceShare = 0,
                SkillsShare = 1,
                RoleInputTokens = new() { ["coder"] = bodyTokens, ["planner"] = bodyTokens * 2 + Encoding.UTF8.GetByteCount(text) }
            };
            File.WriteAllText(Path.Combine(root, "config/context-budget-policy.json"),
                JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            SkillReferenceRequest[] requests = [new("a-match", "one.md"), new("z-match", "one.md"),
                new("a-match", "unknown.md"), new("ineligible", "one.md")];
            var budgeted = await service.ActivateAndLoadAsync(root, context, tokenizers, "fixture", requests);
            Assert.Equal(new[] { "a-match", "z-match" }, budgeted.ActivatedSkillIds);
            Assert.Equal(bodyTokens, budgeted.ConsumedTokens);
            Assert.Equal(0, budgeted.RemainingTokens);
            Assert.Equal(new string?[] { null, "skills-budget-exceeded", "skills-budget-exceeded",
                "instructions-not-loaded", "unknown-reference", "skill-not-activated" },
                budgeted.Evidence.Select(item => item.Load.OmissionReason));
            Assert.Equal(bodyTokens, budgeted.Evidence[1].Tokens);
            Assert.Null(budgeted.Evidence[1].Load.Content);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), budgeted.Evidence[2].Tokens);
            Assert.Equal("Instructions for a-match é.", budgeted.Evidence[0].Load.Content);
            Assert.True(budgeted.Tokenizer.FixtureOnly);
            Assert.Equal("r1", budgeted.Tokenizer.Revision);
            var repeated = await service.ActivateAndLoadAsync(root, context, tokenizers, "fixture", requests);
            Assert.Equal(JsonSerializer.Serialize(budgeted), JsonSerializer.Serialize(repeated));
            var planner = await service.ActivateAndLoadAsync(root, context with { Role = "planner" }, tokenizers, "fixture", requests);
            Assert.Equal(3, planner.Evidence.Count(item => item.Load.Status == "loaded"));
            Assert.Equal(0, planner.RemainingTokens);
            Assert.Equal("skills-budget-exceeded", planner.Evidence[3].Load.OmissionReason);
            Assert.Equal(text, planner.Evidence[2].Load.Content);
            File.WriteAllText(Path.Combine(root, "config/context-budget-policy.json"),
                JsonSerializer.Serialize(policy with { SkillsShare = 0 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var zero = await service.ActivateAndLoadAsync(root, context, tokenizers, "fixture", requests);
            Assert.Equal(0, zero.ConsumedTokens);
            Assert.Equal(0, zero.SkillsTokenBudget);
            Assert.All(zero.Evidence, item => Assert.Equal("omitted", item.Load.Status));
            Assert.Equal("skills-budget-exceeded", zero.Evidence[0].Load.OmissionReason);
            Assert.Equal("instructions-not-loaded", zero.Evidence[2].Load.OmissionReason);
            File.WriteAllText(Path.Combine(root, "config/context-budget-policy.json"),
                JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var options = new EvaluationOptions();
            options.SchemaRegistry.Register(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/tokenizer-manifest.schema.json")));
            options.SchemaRegistry.Register(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-load-result.schema.json")));
            var activationSchema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-activation-load-result.schema.json"));
            foreach (var result in new[] { budgeted, planner, zero })
            {
                result.Validate();
                Assert.True(activationSchema.Evaluate(JsonSerializer.SerializeToNode(result,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!, options).IsValid);
                Assert.Throws<ArgumentException>(() => (result with { ConsumedTokens = result.ConsumedTokens + 1 }).Validate());
                Assert.Throws<ArgumentException>(() => (result with { Evidence = [] }).Validate());
            }
            var invalidEvidence = budgeted.Evidence.ToArray();
            invalidEvidence[1] = invalidEvidence[1] with { Tokens = 0 };
            Assert.Throws<ArgumentException>(() => (budgeted with { Evidence = invalidEvidence }).Validate());
            invalidEvidence = planner.Evidence.ToArray();
            invalidEvidence[0] = invalidEvidence[0] with { Load = invalidEvidence[0].Load with { SkillId = "ineligible" } };
            Assert.Throws<ArgumentException>(() => (planner with { Evidence = invalidEvidence }).Validate());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ActivateAndLoadAsync(root,
                context, tokenizers, "fixture", requests, cancellationToken: new CancellationToken(true)));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ActivateAndLoadAsync(root, context, tokenizers, "missing", requests));
            await Assert.ThrowsAsync<ContextBudgetExceededException>(() => service.ActivateAndLoadAsync(root,
                context, tokenizers, "fixture", requests, modelContextTokens: 1));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ActivateAndLoadAsync(root, context,
                tokenizers, "fixture", [requests[0], requests[0]]));
            foreach (var (id, path, reason) in new[] {
                ("ineligible", "one.md", "skill-not-activated"),
                ("unknown", "one.md", "skill-not-activated"),
                ("z-match", "undeclared.md", "unknown-reference"),
                ("z-match", "script.md", "unknown-reference"),
                ("z-match", "missing.md", "content-unavailable"),
                ("z-match", "../outside.md", "unsafe-path") })
            {
                var result = await service.LoadReferenceAsync(root, context, id, path);
                results.Add(result);
                Assert.Equal("omitted", result.Status);
                Assert.Equal(reason, result.OmissionReason);
                Assert.Null(result.Content);
            }
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/skills/z-match/one.md"), "tampered");
            var mismatch = await service.LoadReferenceAsync(root, context, "z-match", "one.md");
            results.Add(mismatch);
            Assert.Equal("hash-mismatch", mismatch.OmissionReason);
            File.CreateSymbolicLink(Path.Combine(root, "plugins/sdeveng/skills/z-match/linked.md"),
                Path.Combine(root, "plugins/sdeveng/skills/a-match/one.md"));
            var linked = await service.LoadReferenceAsync(root, context, "z-match", "linked.md");
            results.Add(linked);
            Assert.Equal("unsafe-path", linked.OmissionReason);
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-load-result.schema.json"));
            foreach (var result in results)
                Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(result,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!).IsValid);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadInstructionsAsync(root, context,
                "z-match", new CancellationToken(true)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
