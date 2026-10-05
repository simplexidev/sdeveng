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
            new(PromptComponentId.Request, "implementer", "work-request", "prompt/request", "sha256:request",
                RequestContentKind: PromptRequestContentKind.Text,
                RequestProjection: PromptRequestProjection.Condensed,
                OriginalArtifactReference: "work-request/original"),
            new(PromptComponentId.OutputContract, "implementer", "contract", "prompt/output", "sha256:output")
        ]);
        manifest.Validate();

        var json = JsonSerializer.SerializeToNode(manifest, AgentTool.Json)!;
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/prompt-manifest.schema.json"));
        Assert.True(schema.Evaluate(json).IsValid, JsonSerializer.Serialize(json));
        Assert.False(schema.Evaluate(JsonNode.Parse("""{"schemaVersion":1,"components":[]}""")).IsValid);
        Assert.False(schema.Evaluate(JsonNode.Parse("""{"schemaVersion":2,"components":[]}""")).IsValid);
        Assert.Throws<ArgumentException>(() => (manifest with { Components = manifest.Components.Where(component => component.Id != PromptComponentId.Request).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { Components = [.. manifest.Components, manifest.Components[0]] }).Validate());
        var unknownIdentity = JsonNode.Parse(JsonSerializer.Serialize(json))!;
        unknownIdentity["components"]![0]!["id"] = "future-component";
        Assert.False(schema.Evaluate(unknownIdentity).IsValid);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PromptManifest>(JsonSerializer.Serialize(unknownIdentity), AgentTool.Json));
        var missingRequiredField = JsonNode.Parse(JsonSerializer.Serialize(json))!;
        missingRequiredField["components"]![2]!.AsObject().Remove("contentHash");
        Assert.False(schema.Evaluate(missingRequiredField).IsValid);
        Assert.Throws<ArgumentException>(() => (manifest with
        {
            Components = manifest.Components.Select(component => component.Id == PromptComponentId.Request ? component with { ContentHash = " " } : component).ToArray()
        }).Validate());
        Assert.Equal("system", json["components"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("role", json["components"]![1]!["id"]!.GetValue<string>());
        Assert.Equal("text", json["components"]![2]!["requestContentKind"]!.GetValue<string>());
        Assert.Equal("condensed", json["components"]![2]!["requestProjection"]!.GetValue<string>());
        Assert.Equal("work-request/original", json["components"]![2]!["originalArtifactReference"]!.GetValue<string>());

        var factMappingManifest = manifest with
        {
            Components = manifest.Components.Select(component => component.Id == PromptComponentId.Request
                ? component with { RequestContentKind = PromptRequestContentKind.FactMapping, RequestProjection = PromptRequestProjection.Normalized }
                : component).ToArray()
        };
        factMappingManifest.Validate();
        var factMappingJson = JsonSerializer.SerializeToNode(factMappingManifest, AgentTool.Json)!;
        Assert.True(schema.Evaluate(factMappingJson).IsValid, JsonSerializer.Serialize(factMappingJson));
        Assert.Equal("fact-mapping", factMappingJson["components"]![2]!["requestContentKind"]!.GetValue<string>());
        Assert.Equal("normalized", factMappingJson["components"]![2]!["requestProjection"]!.GetValue<string>());
        var incompleteRequestMetadata = factMappingJson.DeepClone();
        incompleteRequestMetadata["components"]![2]!["originalArtifactReference"] = null;
        Assert.False(schema.Evaluate(incompleteRequestMetadata).IsValid);
        Assert.Throws<ArgumentException>(() => (factMappingManifest with
        {
            Components = factMappingManifest.Components.Select(component => component.Id == PromptComponentId.Request
                ? component with { OriginalArtifactReference = null }
                : component).ToArray()
        }).Validate());

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

        // Existing v1 request components without the additive metadata remain readable.
        var legacyManifest = manifest with
        {
            Components = manifest.Components.Select(component => component.Id == PromptComponentId.Request
                ? component with { RequestContentKind = null, RequestProjection = null, OriginalArtifactReference = null }
                : component).ToArray()
        };
        var legacyJson = JsonSerializer.SerializeToNode(legacyManifest, AgentTool.Json)!;
        legacyJson["components"]![2]!.AsObject().Remove("requestContentKind");
        legacyJson["components"]![2]!.AsObject().Remove("requestProjection");
        legacyJson["components"]![2]!.AsObject().Remove("originalArtifactReference");
        var legacy = JsonSerializer.Deserialize<PromptManifest>(JsonSerializer.Serialize(legacyJson), AgentTool.Json)!;
        legacy.Validate();
        // Existing v1 fixtures without the additive isLoaded field remain readable as loaded content.
        Assert.All(legacy.Components, component => Assert.True(component.IsLoaded));
    }
}
