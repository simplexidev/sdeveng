namespace SdevEng.Tests;

public sealed class TriageFactsExtractorTests
{
    [Fact]
    public async Task BuildsDeterministicAggregateWithRawHintsAndUnresolvedFamilies()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var issue = new GitHubIssue(5, "  Fix parser  ", "open", new Uri("https://github.com/acme/widget/issues/5"),
            "See `src/Parser.cs:4` and `Parser.Run()`; area:backend\nacme/other#8", "octocat");

        var first = await TriageFactsExtractor.ExtractIssueAsync(issue, repo.Root);
        var second = await TriageFactsExtractor.ExtractIssueAsync(issue, repo.Root);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.Equal("acme/widget", first.CandidateRepository);
        Assert.Equal("  Fix parser  ", first.Title);
        Assert.Equal(new[] { "src", "area:backend" }, first.AreaHints);
        Assert.Equal(new[] { "type", "area", "risk", "complexity", "scope" }, first.UnresolvedFamilies);
        Assert.Contains(first.Files, file => file.Path == "src/Parser.cs" && file.StartLine == 4);
        Assert.Contains(first.Symbols, symbol => symbol.Value == "Parser.Run()");
        Assert.Contains(first.LinkedReferences, reference => reference.Owner == "acme" && reference.Repository == "other" && reference.Number == 8);
        Assert.DoesNotContain(first.AreaHints, hint => hint.StartsWith("area:", StringComparison.Ordinal) && hint != "area:backend");
    }

    [Fact]
    public async Task AggregateBoundsAllFactFamilies()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var issue = new GitHubIssue(5, "`src/A.cs` `lib/B.cs`", "open", new Uri("https://github.com/acme/widget/issues/5"),
            "`Alpha.Run()` `Beta.Run()` area:one area:two acme/other#8 acme/else#9", null);

        var facts = await TriageFactsExtractor.ExtractIssueAsync(issue, repo.Root, 1);

        Assert.Single(facts.Files);
        Assert.Single(facts.Symbols);
        Assert.Single(facts.LinkedReferences);
        Assert.Single(facts.AreaHints);
        Assert.True(facts.Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TriageFactsExtractor.ExtractIssueAsync(issue, repo.Root, 0));
    }

    [Theory]
    [InlineData("https://github.com/acme/widget/issues/12", "issue", 12)]
    [InlineData("https://github.com/acme/widget/pull/13", "pull-request", 13)]
    [InlineData("acme/other#14", "issue-or-pr", 14)]
    public async Task ExtractsSupportedLinkedReferences(string text, string kind, int number)
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        var facts = await TriageFactsExtractor.ExtractAsync(text, repo.Root);
        Assert.Equal("acme/widget", facts.CandidateRepository);
        Assert.Equal(new LinkedIssueReference(kind == "issue-or-pr" ? "acme" : "acme", kind == "issue-or-pr" ? "other" : "widget", number, kind), Assert.Single(facts.References));
    }

    [Fact]
    public async Task KeepsInvocationTargetAndDeduplicatesInStableOrder()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "git@github.com:acme/widget.git");
        var facts = await TriageFactsExtractor.ExtractAsync("#3 acme/other#4 #3 https://github.com/acme/other/issues/4", repo.Root);
        Assert.Equal("acme/widget", facts.CandidateRepository);
        Assert.Equal(new[] { ("acme", "widget", 3, "issue-or-pr"), ("acme", "other", 4, "issue-or-pr"), ("acme", "other", 4, "issue") },
            facts.References.Select(x => (x.Owner, x.Repository, x.Number, x.Kind)));
    }

    [Theory]
    [InlineData("#0")]
    [InlineData("acme/widget#-1")]
    [InlineData("https://github.com/acme/widget/issues/0")]
    [InlineData("https://github.com/acme/widget/issues/4?x=1")]
    [InlineData("https://github.com/acme/widget/issues/5#fragment")]
    [InlineData("https://github.com/acme/widget/issues/999999999999999999999")]
    public async Task RejectsInvalidIdsAndUrlSuffixes(string text)
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        Assert.Empty((await TriageFactsExtractor.ExtractAsync(text, repo.Root)).References);
    }

    [Fact]
    public async Task BoundsOutputAndLeavesCandidateUnresolvedForMissingOrAmbiguousOrigin()
    {
        using var repo = new TemporaryGitRepository();
        var noOrigin = await TriageFactsExtractor.ExtractAsync("#1", repo.Root);
        Assert.Null(noOrigin.CandidateRepository);
        Assert.Empty(noOrigin.References);

        repo.Run("remote", "add", "origin", "https://github.com/acme/widget.git");
        repo.Run("remote", "set-url", "--add", "origin", "https://github.com/acme/other.git");
        var ambiguous = await TriageFactsExtractor.ExtractAsync("https://github.com/acme/widget/issues/2 https://github.com/acme/other/pull/3", repo.Root, 1);
        Assert.Null(ambiguous.CandidateRepository);
        Assert.Single(ambiguous.References);
        Assert.True(ambiguous.ReferencesTruncated);
        Assert.Equal("issue", ambiguous.References[0].Kind);
    }

    [Fact]
    public async Task RejectsNonGithubOriginForCandidate()
    {
        using var repo = new TemporaryGitRepository();
        repo.Run("remote", "add", "origin", "https://example.com/acme/widget.git");
        var facts = await TriageFactsExtractor.ExtractAsync("#1 acme/elsewhere#2", repo.Root);
        Assert.Null(facts.CandidateRepository);
        Assert.Single(facts.References);
        Assert.Equal("elsewhere", facts.References[0].Repository);
    }
}
