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
