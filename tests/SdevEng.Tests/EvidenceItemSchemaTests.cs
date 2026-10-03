using System.Text.Json.Nodes;
using Json.Schema;

namespace SdevEng.Tests;

public sealed class EvidenceItemSchemaTests
{
    [Fact]
    public void EvidenceItemSchemaAcceptsStableAttributableAndExpandableItem()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/evidence-item.schema.json"));
        var item = JsonNode.Parse("""{"schemaVersion":1,"id":"evidence:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","locationKey":"repo:src/Example.cs#Example.Type","source":{"kind":"repository","identity":"simplexidev/sdeveng@0123456789abcdef0123456789abcdef01234567"},"content":{"excerpt":"relevant line","line":12}}""")!;

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
}
