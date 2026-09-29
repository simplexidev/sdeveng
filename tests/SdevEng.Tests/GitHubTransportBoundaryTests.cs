using System.Text.RegularExpressions;

public sealed class GitHubTransportBoundaryTests
{
    [Fact]
    public void ProductionGitHubTransportIsOwnedByTypedAbstractionsAndDoctorProbe()
    {
        var source = File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "tools/AgentTool.cs"));

        // API endpoint construction belongs to the typed provider/readers. A product command
        // that adds an endpoint must go through one of these abstractions.
        var apiOwners = new HashSet<string>(StringComparer.Ordinal)
        {
            "GitHubPrStatusReader", "GitHubReviewCommentReader",
            "GitHubCommitReader", "GitHubRepositoryMetadataReader", "GitHubIssueReader",
            "GitHubActionsReader", "GitHubChecksWorkflowReader"
        };
        var endpointOwners = Regex.Matches(source, @"(?m)^\s*static readonly Uri ApiRoot = new\(""https://api\.github\.com/""\);")
            .Cast<Match>()
            .Select(match => EnclosingType(source, match.Index));
        Assert.Equal(apiOwners, endpointOwners.ToHashSet(StringComparer.Ordinal));

        var hostOwners = Regex.Matches(source, @"https://api\.github\.com")
            .Cast<Match>()
            .Select(match => EnclosingType(source, match.Index))
            .ToArray();
        Assert.NotEmpty(hostOwners);
        Assert.All(hostOwners, owner => Assert.Contains(owner, apiOwners));

        // Raw HTTP calls aimed at GitHub bypass IGitHubReadClient/GitHubTransport. The
        // provider's HttpClient.GetAsync is the sole allowlisted raw GitHub read transport.
        var rawHttpCalls = Regex.Matches(source,
            @"\b(?:http|client|new HttpClient\([^\n)]*\))\.(?:GetAsync|GetStringAsync|SendAsync|PostAsync|PutAsync|DeleteAsync)\s*\(")
            .Cast<Match>()
            .Where(match => EnclosingType(source, match.Index).StartsWith("GitHub", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            new[] { "GitHubReadClient", "GitHubTransport" },
            rawHttpCalls.Select(match => EnclosingType(source, match.Index)).OrderBy(name => name, StringComparer.Ordinal));

        // The doctor auth probe is explicitly outside typed product reads. No product
        // orchestration may add gh api/pr/issue/etc. subprocess transport.
        var ghCalls = Regex.Matches(source, @"Processes\.Run\(\s*""gh""\s*,\s*\[(?<args>[^\]]*)\]")
            .Cast<Match>()
            .ToArray();
        Assert.Single(ghCalls);
        Assert.Contains("auth", ghCalls[0].Groups["args"].Value, StringComparison.Ordinal);
        Assert.Contains("status", ghCalls[0].Groups["args"].Value, StringComparison.Ordinal);
        Assert.Equal("DoctorCommandModule", EnclosingType(source, ghCalls[0].Index));
    }

    static string EnclosingType(string source, int position)
    {
        var declarations = Regex.Matches(source,
            @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)[^\{;]*?\{")
            .Cast<Match>()
            .Where(match => match.Index < position)
            .ToArray();
        foreach (var declaration in declarations.Reverse())
        {
            var depth = 0;
            for (var index = source.IndexOf('{', declaration.Index); index <= position; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}') depth--;
            }
            if (depth > 0) return declaration.Groups["name"].Value;
        }
        return "";
    }
}
