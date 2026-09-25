using System.Text.Json.Nodes;

namespace SdevEng.Tests;

public class EvaluationTests
{
    [Fact]
    public void MeasuredRunChecksCorrectnessAndTokenRegression()
    {
        using var repo = new TemporaryGitRepository();
        var path = Path.Combine(repo.Root, "results.json");
        var run = new JsonObject { ["skill"] = "dotnet-test-quality", ["success"] = true, ["expectedSatisfied"] = true, ["safetySatisfied"] = true, ["revision"] = "test-revision", ["model"] = "test-model", ["promptHash"] = "test-prompt", ["tokens"] = 1200, ["turns"] = 3, ["toolCalls"] = 4, ["elapsedSeconds"] = 12, ["fileReads"] = 2, ["unnecessaryBroadOperations"] = 0, ["baseline"] = new JsonObject { ["success"] = true, ["tokens"] = 1800 } };
        void Write() => File.WriteAllText(path, new JsonArray(run.DeepClone()).ToJsonString());
        Write(); Assert.Equal(0, Evaluation.Run(AgentTool.FindToolkit(), "dotnet-test-quality", path).ExitCode);
        run["tokens"] = 3000; Write(); Assert.Equal(1, Evaluation.Run(AgentTool.FindToolkit(), "dotnet-test-quality", path).ExitCode);
        run["tokens"] = 1000; run["success"] = false; Write(); Assert.Equal(1, Evaluation.Run(AgentTool.FindToolkit(), "dotnet-test-quality", path).ExitCode);
    }
    [Fact]
    public void MissingMeasurementCannotPass()
    {
        using var repo = new TemporaryGitRepository(); var path = Path.Combine(repo.Root, "results.json");
        File.WriteAllText(path, "[{\"skill\":\"dotnet-test-quality\",\"success\":true,\"baseline\":{\"success\":true,\"tokens\":1000}}]");
        Assert.Equal(1, Evaluation.Run(AgentTool.FindToolkit(), "dotnet-test-quality", path).ExitCode);
    }
}
