using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class EvidenceItemSchemaTests
{
    [Fact]
    public void EvidenceItemSchemaAcceptsStableAttributableAndExpandableItem()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-item.schema.json"));
        var item = JsonNode.Parse("""{"schemaVersion":1,"id":"evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","locationKey":"repo:src/Example.cs#Example.Type","source":{"kind":"repository","identity":"simplexidev/sdeveng@0123456789abcdef0123456789abcdef01234567","uri":"https://github.com/simplexidev/sdeveng/blob/0123456789abcdef0123456789abcdef01234567/src/Example.cs","revision":"0123456789abcdef0123456789abcdef01234567","capturedAt":"2026-10-03T12:00:00Z","attributedTo":"reviewer"},"content":{"excerpt":"relevant line","line":12}}""")!;

        Assert.True(schema.Evaluate(item).IsValid);
    }

    [Theory]
    [InlineData("repo:src/Example.cs", "evidence:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("src/Example.cs", "evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("repo:../Example.cs", "evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void EvidenceItemSchemaRejectsUnstableIdentityOrLocation(string locationKey, string id)
    {
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-item.schema.json"));
        var item = JsonNode.Parse("""{"schemaVersion":1,"id":"evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","locationKey":"repo:src/Example.cs","source":{"kind":"repository","identity":"simplexidev/sdeveng"},"content":{"excerpt":"relevant line"}}""")!;
        item["locationKey"] = locationKey;
        item["id"] = id;

        Assert.False(schema.Evaluate(item).IsValid);
    }

    [Fact]
    public void EvidencePackSchemaAcceptsRoleAndEvidenceItems()
    {
        var itemSchema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-item.schema.json"));
        SchemaRegistry.Global.Register(itemSchema);
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-pack.schema.json"));
        var pack = JsonNode.Parse("""{"schemaVersion":1,"role":"implementer","items":[{"schemaVersion":1,"id":"evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","locationKey":"repo:src/Example.cs","source":{"kind":"repository","identity":"simplexidev/sdeveng@0123456789abcdef0123456789abcdef01234567"},"content":{"excerpt":"relevant line"}}],"content":{"requestId":"work-123"}}""")!;

        Assert.True(schema.Evaluate(pack).IsValid);
        pack["role"] = "";
        Assert.False(schema.Evaluate(pack).IsValid);
    }
}
