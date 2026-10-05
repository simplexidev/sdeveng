using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng;
using SdevEng.Infrastructure;

public sealed class ChatTemplateManifestTests
{
    [Fact]
    public void RegistryValidatesSchemaIdentityAndRendersCanonicalMessagesToolsAndPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "chat-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var asset = Encoding.UTF8.GetBytes("fixture tokens");
            File.WriteAllBytes(Path.Combine(root, "tokens.bin"), asset);
            var assetHash = Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant();
            var tokenizer = new TokenizerManifest(1, "tok", "fixture-model", "fixture", "r1", [new("tokens.bin", assetHash)], "fixture", [], "fixture-v1", true);
            var tokenRegistry = new TokenizerRegistry(root);
            tokenRegistry.Register(tokenizer);
            var template = new ChatTemplateManifest(1, "template", "r1", new string('0', 64), "tok", "r1", "fixture", "<m>", "|", "</m>", "\n", "<tools>", "</tools>", "<assistant>", true);
            template = template with { Checksum = template.ComputeChecksum() };
            template.Validate();
            var json = JsonSerializer.SerializeToNode(template, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/chat-template-manifest.schema.json"));
            Assert.True(schema.Evaluate(json).IsValid, json.ToJsonString());
            var malformed = json.DeepClone(); malformed.AsObject().Remove("generationPrefix");
            Assert.False(schema.Evaluate(malformed).IsValid);
            var bad = template with { Checksum = new string('f', 64) };
            Assert.Throws<ArgumentException>(() => bad.Validate());

            var prompt = new PromptManifest(1,
            [
                new(PromptComponentId.System, "system", "test", "s", "h"),
                new(PromptComponentId.Role, "assistant", "test", "r", "h"),
                new(PromptComponentId.Request, "user", "test", "q", "h"),
                new(PromptComponentId.Tools, "tools", "test", "tools", "h", Tools: [new("z", "Z", "desc", "z-schema"), new("a", "A", "desc", "a-schema")]),
                new(PromptComponentId.OutputContract, "assistant", "test", "o", "h", ResponseSchema: "schema")
            ]);
            var registry = new ChatTemplateRegistry(tokenRegistry);
            var metadataPath = Path.Combine(root, "chat-template.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(template, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(template, ChatTemplateRegistry.ReadMetadata(metadataPath));
            Assert.Equal(template, registry.RegisterFile(metadataPath));
            Assert.Throws<ArgumentException>(() => registry.Register(template));
            Assert.Throws<ArgumentException>(() => registry.Register(template with { Checksum = template.ComputeChecksum(), TokenizerRevision = "missing" }));
            var text = new Dictionary<string, string> { ["s"] = "sys", ["r"] = "role", ["q"] = "ask", ["o"] = "json" };
            var rendered = registry.Render(template.Id, template.Revision, template.Checksum, prompt, text);
            Assert.True(rendered.Available);
            Assert.Equal("<m>system|sys</m>\n<m>assistant|role</m>\n<m>user|ask</m>\n<m>tools|<tools>[{\"description\":\"desc\",\"id\":\"a\",\"name\":\"A\",\"schemaReference\":\"a-schema\",\"type\":\"function\"},{\"description\":\"desc\",\"id\":\"z\",\"name\":\"Z\",\"schemaReference\":\"z-schema\",\"type\":\"function\"}]</tools></m>\n<m>assistant|json</m><assistant>", rendered.Text);
            Assert.False(registry.Render("missing", "r1", template.Checksum, prompt, text).Available);
            Assert.False(registry.Render(template.Id, template.Revision, template.Checksum, prompt, new Dictionary<string, string>()).Available);
            var originalPrompt = JsonSerializer.Serialize(prompt);
            var originalText = text.ToArray();
            var reordered = prompt with
            {
                Components = prompt.Components.Select(component => component.Id == PromptComponentId.Tools
                    ? component with { Tools = component.Tools!.Reverse().ToArray() }
                    : component).ToArray()
            };
            var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "tr-TR" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    Assert.Equal(rendered, registry.Render(template.Id, template.Revision, template.Checksum, reordered, text));
                    Assert.Equal(rendered, registry.Render(template.Id, template.Revision, template.Checksum, prompt, text));
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }

            var invalidTools = prompt with
            {
                Components = prompt.Components.Select(component => component.Id == PromptComponentId.Tools
                    ? component with { Tools = [component.Tools![0], component.Tools[0]] }
                    : component).ToArray()
            };
            Assert.Equal(new ChatTemplateRenderResult(false, null, "chat-template-or-tokenizer-unavailable"),
                registry.Render(template.Id, template.Revision, template.Checksum, invalidTools, text));
            Assert.Equal(rendered, registry.Render(template.Id, template.Revision, template.Checksum, prompt, text));
            Assert.Equal(originalPrompt, JsonSerializer.Serialize(prompt));
            Assert.Equal(originalText, text.ToArray());

            File.Delete(Path.Combine(root, "tokens.bin"));
            Assert.Equal(new ChatTemplateRenderResult(false, null, "chat-template-or-tokenizer-unavailable"),
                registry.Render(template.Id, template.Revision, template.Checksum, prompt, text));
            File.WriteAllBytes(Path.Combine(root, "tokens.bin"), asset);
            Assert.Equal(rendered, registry.Render(template.Id, template.Revision, template.Checksum, prompt, text));
        }
        finally { Directory.Delete(root, true); }
    }
}
