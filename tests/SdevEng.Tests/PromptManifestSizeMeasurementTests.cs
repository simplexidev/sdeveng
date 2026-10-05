using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng;

public class PromptManifestSizeMeasurementTests
{
    [Fact]
    public void MeasurementCountsComponentTextAndArtifactWriterEmitsVersionedProjection()
    {
        var manifest = new PromptManifest(1,
        [
            new(PromptComponentId.System, "system", "policy", "system", "hash"),
            new(PromptComponentId.Role, "role", "role-source", "role", "hash"),
            new(PromptComponentId.Request, "role", "request", "request", "hash"),
            new(PromptComponentId.OutputContract, "role", "contract", "output", "hash", ResponseSchema: "schema:v1")
        ]);
        var measured = PromptManifestSizeMeasurement.Measure(manifest, new Dictionary<string, string>
        {
            ["system"] = "é😀\r\nx\ry",
            ["role"] = "",
            ["request"] = "abc",
            ["output"] = ""
        });

        Assert.Equal("manifest-text-projection", measured.MeasurementKind);
        Assert.Equal("manifest-text-projection/v1", measured.RendererRevision);
        Assert.Equal("complete-manifest-text-projection", measured.CompletePrompt!.MeasurementKind);
        Assert.Equal((long)14, measured.CompletePrompt.Utf8Bytes);
        Assert.Equal((long)10, measured.CompletePrompt.UnicodeScalarValues);
        Assert.Equal((long)3, measured.CompletePrompt.Lines);
        Assert.Null(measured.CompletePrompt.UnavailableReason);
        Assert.Equal("prompt-manifest-size-measurement", measured.Kind);
        Assert.Equal(PromptComponentId.System, measured.Components[0].ComponentId);
        Assert.Equal("system", measured.Components[0].ContentReference);
        Assert.Equal("policy", measured.Components[0].Provenance);
        Assert.Equal((long)11, measured.Components[0].Utf8Bytes);
        Assert.Equal((long)7, measured.Components[0].UnicodeScalarValues);
        Assert.Equal((long)3, measured.Components[0].Lines);
        Assert.Equal((long)0, measured.Components[1].Utf8Bytes);
        Assert.Equal((long)0, measured.Components[1].UnicodeScalarValues);
        Assert.Equal((long)0, measured.Components[1].Lines);
        Assert.Null(measured.Components[3].UnavailableReason);
        Assert.Equal((long)0, measured.Components[3].Utf8Bytes);

        var path = Path.Combine(Path.GetTempPath(), $"prompt-size-{Guid.NewGuid():N}.json");
        try
        {
            Artifacts.WritePromptManifestSizeMeasurement(path, measured);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/prompt-manifest-size-measurement.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid, JsonSerializer.Serialize(json));
            Assert.Equal("system", json["components"]![0]!["componentId"]!.GetValue<string>());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MeasurementPreservesBytesAndCountsUnicodeScalarsAndNormalizedLines()
    {
        const string value = "A\r\né\r😀";
        var manifest = new PromptManifest(1,
        [
            new(PromptComponentId.System, "system", "policy", "system", "hash"),
            new(PromptComponentId.Role, "role", "role", "role", "hash"),
            new(PromptComponentId.Request, "role", "request", "request", "hash"),
            new(PromptComponentId.OutputContract, "role", "contract", "output", "hash", ResponseSchema: "schema:v1")
        ]);
        var item = PromptManifestSizeMeasurement.Measure(manifest, new Dictionary<string, string> { ["system"] = value }).Components[0];
        Assert.Equal((long)10, item.Utf8Bytes);
        Assert.Equal((long)6, item.UnicodeScalarValues);
        Assert.Equal((long)3, item.Lines);
        Assert.Equal((long)0, PromptManifestSizeMeasurement.Measure(manifest,
            new Dictionary<string, string> { ["system"] = "" }).Components[0].Lines);
        Assert.Equal((long)1, PromptManifestSizeMeasurement.Measure(manifest,
            new Dictionary<string, string> { ["system"] = "single line" }).Components[0].Lines);
        Assert.Equal((long)3, PromptManifestSizeMeasurement.Measure(manifest,
            new Dictionary<string, string> { ["system"] = "a\nb\n" }).Components[0].Lines);
        var unavailableManifest = manifest with { Components = manifest.Components.Select(component => component.Id == PromptComponentId.System ? component with { IsLoaded = false } : component).ToArray() };
        Assert.Equal("component-text-unavailable", PromptManifestSizeMeasurement.Measure(unavailableManifest, new Dictionary<string, string> { ["system"] = value }).Components[0].UnavailableReason);
        var unavailable = PromptManifestSizeMeasurement.Measure(unavailableManifest, new Dictionary<string, string> { ["system"] = value });
        Assert.Equal("component-text-unavailable", unavailable.CompletePrompt!.UnavailableReason);
        Assert.Null(unavailable.CompletePrompt.Utf8Bytes);
    }
}
