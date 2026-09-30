public sealed class IssueReferenceExtractorTests
{
    [Fact]
    public void ExtractsExplicitFilesAndSymbolsInStableOrder()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        File.WriteAllText(Path.Combine(repo.Path, "src", "Thing.cs"), "class Thing {}");
        var issue = Issue("`src\\Thing.cs:12` then `Acme.Widget.Run()` and [docs](docs/guide.md#L3-L5), `src/Thing.cs:12` `Missing.cs#L8`");

        var facts = IssueReferenceExtractor.Extract(issue, repo.Path, 10);

        Assert.Equal(new[] { "src/Thing.cs", "docs/guide.md", "Missing.cs" }, facts.Files.Select(file => file.Path));
        Assert.Equal(new int?[] { 12, 3, 8 }, facts.Files.Select(file => file.StartLine));
        Assert.Equal(new int?[] { 12, 5, 8 }, facts.Files.Select(file => file.EndLine));
        Assert.Equal(new[] { true, false, false }, facts.Files.Select(file => file.Exists));
        Assert.Equal(new[] { "Acme.Widget.Run()" }, facts.Symbols.Select(symbol => symbol.Value));
    }

    [Fact]
    public void RejectsAbsoluteTraversalUriAndMalformedReferences()
    {
        using var repo = new TempDirectory();
        var issue = Issue("`/etc/passwd` `C:\\secret.cs` `../secret.cs` `src/../secret.cs` `https://example.com/x.cs` `not a symbol` [web](https://example.com/a.cs) [bad](../outside.md)");

        var facts = IssueReferenceExtractor.Extract(issue, repo.Path, 10);

        Assert.Empty(facts.Files);
        Assert.Empty(facts.Symbols);
    }

    [Fact]
    public void BoundsEachCollectionIndependently()
    {
        using var repo = new TempDirectory();
        var issue = Issue("`A.One` `B.Two` `one.cs` `two.cs` `three.cs`");

        var facts = IssueReferenceExtractor.Extract(issue, repo.Path, 2);

        Assert.Equal(new[] { "one.cs", "two.cs" }, facts.Files.Select(file => file.Path));
        Assert.Equal(new[] { "A.One", "B.Two" }, facts.Symbols.Select(symbol => symbol.Value));
    }

    static GitHubIssue Issue(string body) => new(1, "references", "open", new Uri("https://github.com/o/r/issues/1"), body, null);

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdeveng-issue-reference-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
