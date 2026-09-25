using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public class DotnetSkillsDriftTests
{
    static JsonNode Manifest() => JsonNode.Parse("""
    { "snapshot": { "repository": "dotnet/skills", "commit": "0123456789012345678901234567890123456789" },
      "decisions": [{ "upstreamPaths": ["plugins/dotnet-test/skills/run-tests/SKILL.md"] }] }
    """)!;
    static JsonNode Comparison(string files) => JsonNode.Parse($$"""{ "head_commit": { "sha": "abcdef" }, "files": {{files}} }""")!;

    [Fact]
    public void NoChangeIsDeterministic() => Assert.Equal("no-change", DotnetSkillsDrift.Analyze(Manifest(), Comparison("[]"))["classification"]!.GetValue<string>());

    [Fact]
    public void RelevantAndIrrelevantChangesAreSeparated()
    {
        var report = DotnetSkillsDrift.Analyze(Manifest(), Comparison("""[{ "filename": "README.md", "status": "modified" }, { "filename": "plugins/dotnet-test/skills/run-tests/SKILL.md", "status": "modified" }]"""));
        Assert.Equal("relevant", report["classification"]!.GetValue<string>());
        Assert.Equal(1, report["irrelevantCount"]!.GetValue<int>());
        Assert.Equal("modified", report["relevant"]![0]!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("removed", null)]
    [InlineData("renamed", "plugins/dotnet-test/skills/run-tests/SKILL.md")]
    public void MovedOrDeletedDecisionPathsRequireReview(string status, string? previous)
    {
        var previousJson = previous is null ? "" : $", \"previous_filename\": \"{previous}\"";
        var path = status == "removed" ? "plugins/dotnet-test/skills/run-tests/SKILL.md" : "plugins/dotnet-test/skills/renamed/SKILL.md";
        var report = DotnetSkillsDrift.Analyze(Manifest(), Comparison($"[{{ \"filename\": \"{path}\", \"status\": \"{status}\"{previousJson} }}]"));
        Assert.Equal("review-required", report["classification"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedAuthoritativeResponseFailsClosed() => Assert.Throws<FormatException>(() => DotnetSkillsDrift.Analyze(Manifest(), JsonNode.Parse("{ \"files\": [{ \"filename\": \"x\" }] }")!));
}
