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
        // Only Doctor may inspect auth status, and the typed authorization probe may use
        // auth status plus authenticated GETs. No other GitHub CLI transport is allowed.
        var ghCalls = Regex.Matches(source, @"(?:Processes|process)\.Run\(\s*""gh""\s*,\s*\[(?<args>[^\]]*)\]")
            .Cast<Match>()
            .ToArray();
        Assert.Equal(4, ghCalls.Length);
        Assert.Single(ghCalls, match => EnclosingType(source, match.Index) == "DoctorCommandModule");
        Assert.All(ghCalls.Where(match => EnclosingType(source, match.Index) == "GitHubAuthorizationProbe"), match =>
        {
            var args = match.Groups["args"].Value;
            Assert.True(args.Contains("auth", StringComparison.Ordinal) && args.Contains("status", StringComparison.Ordinal) ||
                args.Contains("api", StringComparison.Ordinal) && args.Contains("GET", StringComparison.Ordinal));
            Assert.DoesNotContain("POST", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PATCH", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", args, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(ghCalls, match => Assert.Contains(EnclosingType(source, match.Index), new[] { "DoctorCommandModule", "GitHubAuthorizationProbe" }));
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
