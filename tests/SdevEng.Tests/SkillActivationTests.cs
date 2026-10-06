using System.Text.Json;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class SkillActivationTests
{
    [Fact]
    public void CallableCatalogBoundaryFiltersAndOrdersWithoutLoadingContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-activation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "plugins/sdeveng"));
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/plugin.json"), "{\"version\":\"3.0.0\"}");
            var definitions = new Dictionary<string, string>
            {
                ["z-match"] = "activation: '[{\"id\":\"build\"}]'\n",
                ["a-match"] = "activation: '[{\"id\":\"build\"}]'\n",
                ["exact"] = "activation: '[{\"id\":\"dotnet\",\"frameworkVersion\":\"10.0.0\"}]'\n",
                ["wrong-version"] = "activation: '[{\"id\":\"dotnet\",\"frameworkVersion\":\"9.0.0\"}]'\n",
                ["wrong-role"] = "activation: '[{\"id\":\"build\"}]'\nsupportedRoles: '[\"reviewer\"]'\n",
                ["missing-tool"] = "activation: '[{\"id\":\"build\"}]'\nrequiredTools: '[\"unavailable\"]'\n",
                ["unrequested"] = "activation: '[{\"id\":\"other\"}]'\n",
                ["legacy"] = "",
                ["tool"] = "activation: '[{\"id\":\"compile\"}]'\nrequiredTools: '[\"compile\"]'\n"
            };
            var entries = definitions.Select(pair =>
            {
                var path = $"plugins/sdeveng/skills/{pair.Key}/SKILL.md";
                var directory = Path.GetDirectoryName(Path.Combine(root, path))!;
                Directory.CreateDirectory(Path.Combine(directory, "agents"));
                File.WriteAllText(Path.Combine(directory, "agents/openai.yaml"), "interface: {}");
                // Unavailable references and a large body must not affect activation.
                File.WriteAllText(Path.Combine(root, path), $"---\nname: {pair.Key}\n{pair.Value}resources: '[{{\"path\":\"missing.md\",\"type\":\"reference\",\"hash\":\"sha256:{new string('0', 64)}\"}}]'\n---\n" + new string('x', 100_000));
                return new SkillCompatibilityEntry(pair.Key, path, pair.Key, path, "3.0.0", [], "retained");
            }).ToArray();
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(
                new SkillCompatibilityMap("../schemas/skill-compatibility-map.schema.json", 1, entries),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var service = new SkillActivationService();
            var facts = new SkillActivationContext("coder", ["build"], ["compile"], ["compile"], [new("dotnet", "10.0.0")]);
            var expected = new[] { "exact", "a-match", "tool", "z-match" };
            Assert.Equal(expected, service.Activate(root, facts).Select(skill => skill.Id));
            Assert.Equal(expected, service.Activate(root, facts).Select(skill => skill.Id));
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-metadata.schema.json"));
            foreach (var skill in service.Activate(root, facts))
                Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(skill, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                })!).IsValid);
            Assert.DoesNotContain(service.Activate(root, facts with { AvailableTools = [] }), skill => skill.Id == "tool");
            Assert.Throws<ArgumentException>(() => service.Activate(root, facts with { Role = "unknown" }));
            Assert.Throws<ArgumentException>(() => service.Activate(root, facts with { Frameworks = [new("dotnet", "10.0.0"), new("dotnet", "9.0.0")] }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
