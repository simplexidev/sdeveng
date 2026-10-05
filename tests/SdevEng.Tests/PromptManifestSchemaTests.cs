using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng;

public class PromptManifestSchemaTests
{
    [Fact]
    public void PromptManifestSerializesAndValidatesAgainstItsSchema()
    {
        var manifest = new PromptManifest(1,
        [
            new(PromptComponentId.System, "system", "policy", "prompt/system", "sha256:system"),
            new(PromptComponentId.Role, "implementer", "work-unit", "prompt/role", "sha256:role"),
            new(PromptComponentId.Request, "implementer", "work-request", "prompt/request", "sha256:request"),
            new(PromptComponentId.OutputContract, "implementer", "contract", "prompt/output", "sha256:output")
        ]);
        manifest.Validate();

        var json = JsonSerializer.SerializeToNode(manifest, AgentTool.Json)!;
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/prompt-manifest.schema.json"));
        Assert.True(schema.Evaluate(json).IsValid, JsonSerializer.Serialize(json));
        Assert.False(schema.Evaluate(JsonNode.Parse("""{"schemaVersion":1,"components":[]}""")).IsValid);
        Assert.False(schema.Evaluate(JsonNode.Parse("""{"schemaVersion":2,"components":[]}""")).IsValid);
        Assert.Equal("system", json["components"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("role", json["components"]![1]!["id"]!.GetValue<string>());

        var skillManifest = manifest with
        {
            Components =
            [
                .. manifest.Components.Take(2),
                new(PromptComponentId.SkillMetadata, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/metadata", "sha256:metadata"),
                new(PromptComponentId.SkillInstructions, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/SKILL.md", "sha256:instructions"),
                new(PromptComponentId.SkillReferences, "implementer", "skill:alpha/1.0", "skills/alpha/1.0/reference.md", "sha256:reference", IsLoaded: false),
                .. manifest.Components.Skip(2)
            ]
        };
        skillManifest.Validate();
        var skillJson = JsonSerializer.SerializeToNode(skillManifest, AgentTool.Json)!;
        Assert.True(schema.Evaluate(skillJson).IsValid, JsonSerializer.Serialize(skillJson));
        Assert.Equal("skill-metadata", skillJson["components"]![2]!["id"]!.GetValue<string>());
        Assert.False(skillJson["components"]![4]!["isLoaded"]!.GetValue<bool>());

        // Existing v1 fixtures without the additive isLoaded field remain readable as loaded content.
        var legacy = JsonSerializer.Deserialize<PromptManifest>(JsonSerializer.Serialize(manifest, AgentTool.Json), AgentTool.Json)!;
        Assert.All(legacy.Components, component => Assert.True(component.IsLoaded));
    }
}
