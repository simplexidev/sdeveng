using System.Text.Json;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class SkillActivationTests
{
    [Fact]
    public async Task AvaloniaSkillActivatesAndLoadsOnlyForVerifiedPackageVersion()
    {
        var root = AgentTool.FindToolkit();
        var metadata = SkillCompatibilityMapReader.ReadMetadataIndex(root)
            .Single(skill => skill.Id == "avalonia");
        Assert.Equal(new[] { new SkillActivationCondition("avalonia", "11.3.0") }, metadata.Activation);
        Assert.Empty(metadata.Resources!);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
        var service = new SkillActivationService();
        SkillActivationContext Context(string? version) => new("coder", [], [], [],
            version is null ? [] : [new("avalonia", version)]);
        Assert.Contains(service.Activate(root, Context("11.3.0")), skill => skill.Id == "avalonia");
        Assert.DoesNotContain(service.Activate(root, Context("12.0.0")), skill => skill.Id == "avalonia");
        Assert.DoesNotContain(service.Activate(root, Context(null)), skill => skill.Id == "avalonia");
        var body = SkillCompatibilityMapReader.ReadSelectedSkill(root, "avalonia", "test", "1", "coder");
        Assert.Contains("11.3.0", body, StringComparison.Ordinal);
        Assert.Contains("dotnet-skills-provenance.md", body, StringComparison.Ordinal);
        Assert.Contains("code-only", body, StringComparison.Ordinal);
        var schema = JsonSchema.FromFile(Path.Combine(root, "schemas/skill-metadata.schema.json"));
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToNode(metadata, new JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        })!).IsValid);
        var loaded = await service.LoadReferenceAsync(root, Context("11.3.0"), "avalonia", "references/lifetime.md");
        Assert.Equal("unknown-reference", loaded.OmissionReason);
    }

    [Fact]
    public void SystemCommandLineSkillLoadsOnlyForResolvedVersionAndItsReferenceHashIsValid()
    {
        var root = AgentTool.FindToolkit();
        var metadata = SkillCompatibilityMapReader.ReadMetadataIndex(root)
            .Single(skill => skill.Id == "system-commandline");
        Assert.Equal(new[] { new SkillActivationCondition("system-commandline", "2.0.0") }, metadata.Activation);
        SkillCompatibilityMapReader.ValidateResources(root, [metadata]);
        var service = new SkillActivationService();
        SkillActivationContext Context(string? version) => new("coder", [], [], [],
            version is null ? [] : [new("system-commandline", version)]);
        Assert.Contains(service.Activate(root, Context("2.0.0")), skill => skill.Id == "system-commandline");
        Assert.DoesNotContain(service.Activate(root, Context("1.0.0")), skill => skill.Id == "system-commandline");
        Assert.DoesNotContain(service.Activate(root, Context(null)), skill => skill.Id == "system-commandline");
        var body = SkillCompatibilityMapReader.ReadSelectedSkill(root, "system-commandline", "test", "1", "coder");
        Assert.Contains("2.0.0", body, StringComparison.Ordinal);
        Assert.Contains("references/api-generation.md", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public async Task CallableCatalogBoundaryFiltersAndOrdersWithoutLoadingContent(int extraCandidates)
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
            for (var index = 0; index < extraCandidates; index++)
                definitions.Add($"extra-{index:D2}", "activation: '[{\"id\":\"build\"}]'\n");
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
            var expected = new[] { "exact" }.Concat(definitions.Keys
                .Where(id => id is "a-match" or "tool" or "z-match" || id.StartsWith("extra-", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)).ToArray();
            Assert.Equal(expected, service.Activate(root, facts).Select(skill => skill.Id));
            Assert.Equal(expected, service.Activate(root, facts).Select(skill => skill.Id));
            Assert.Equal(expected, (await service.ActivateAsync(root, facts)).Select(skill => skill.Id));
            foreach (var confidence in new double?[] { null, .79, .80 })
            {
                var provider = new RecordingTieBreak(confidence);
                var ranked = await new SkillActivationService(provider).ActivateAsync(root, facts);
                var accepted = confidence == .80 && extraCandidates == 0;
                Assert.Equal(accepted ? new[] { "exact", "z-match", "a-match", "tool" } : expected,
                    ranked.Select(skill => skill.Id));
                if (extraCandidates == 0)
                {
                    var input = Assert.Single(provider.Inputs);
                    Assert.Equal(new[] { "a-match", "tool", "z-match" }, input.Candidates.Select(candidate => candidate.Id));
                    Assert.All(input.Candidates, candidate => Assert.Equal(candidate.Id, candidate.Text));
                }
                else Assert.Empty(provider.Inputs);
            }
            Assert.Equal(expected, (await new SkillActivationService(new RecordingTieBreak(.80),
                new RelevanceRankingPolicy { MinConfidence = .90 }).ActivateAsync(root, facts)).Select(skill => skill.Id));
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-metadata.schema.json"));
            foreach (var skill in await service.ActivateAsync(root, facts))
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

    private sealed class RecordingTieBreak(double? confidence) : ISkillSemanticTieBreakProvider
    {
        public List<RelevanceRankingInput> Inputs { get; } = [];

        public Task<IReadOnlyList<RelevanceRankingScore>> RankAsync(
            RelevanceRankingInput input, CancellationToken cancellationToken = default)
        {
            input.Validate();
            Inputs.Add(input);
            return Task.FromResult<IReadOnlyList<RelevanceRankingScore>>([
                new("z-match", 1) { Confidence = confidence },
                new("wrong-role", 1) { Confidence = 1 },
                new("wrong-version", 1) { Confidence = 1 },
                new("missing-tool", 1) { Confidence = 1 },
                new("exact", 0) { Confidence = 1 }
            ]);
        }
    }
}
