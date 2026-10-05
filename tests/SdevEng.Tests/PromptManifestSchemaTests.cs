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
    }
}
