namespace SdevEng.Tests;

public class OutputCompactionTests
{
    [Fact] public void BoundsDeduplicatesAndRedacts() { var output = Output.Compact("error password=hide\nerror password=hide\n" + new string('a', 100) + "\nextra", new() { MaxLines = 2, MaxLineLength = 20 }); Assert.Equal(2, output.Length); Assert.DoesNotContain("hide", string.Join('\n', output)); Assert.True(output[1].Length <= 21); }
    [Fact] public void SummariesRetainFailureAndFullArtifact() { using var repo = new TemporaryGitRepository(); var file = Path.Combine(repo.Root, "build.log"); File.WriteAllText(file, string.Join('\n', Enumerable.Repeat("noise", 100)) + "\nerror CS1002: expected ;\n"); var json = System.Text.Json.JsonSerializer.Serialize(Output.SummarizeFile(file, new())); Assert.Contains("CS1002", json); Assert.Contains("101", json); Assert.True(File.ReadAllLines(file).Length > 100); }
    [Fact] public void CountsTransitiveAuditFindings() => Assert.Equal(1, Audit.Count(System.Text.Json.Nodes.JsonNode.Parse("{\"projects\":[{\"frameworks\":[{\"transitivePackages\":[{\"vulnerabilities\":[{\"severity\":\"High\"}]}]}]}]}")));
}
