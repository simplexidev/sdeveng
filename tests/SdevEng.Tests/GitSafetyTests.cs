namespace SdevEng.Tests;

public class GitSafetyTests
{
    [Fact] public async Task DirtyWorkIsPreserved() { using var repo = new TemporaryGitRepository(); repo.Write("user.txt", "keep"); await Assert.ThrowsAsync<InvalidOperationException>(() => Git.EnsureSafe(repo.Root, true)); Assert.Equal("keep", File.ReadAllText(Path.Combine(repo.Root, "user.txt"))); }
    [Theory]
    [InlineData("MERGE_HEAD")]
    [InlineData("CHERRY_PICK_HEAD")]
    [InlineData("REVERT_HEAD")]
    [InlineData("BISECT_LOG")]
    [InlineData("index.lock")]
    public async Task RefusesUnfinishedOperations(string marker) { using var repo = new TemporaryGitRepository(); File.WriteAllText(Path.Combine(repo.Root, ".git", marker), "fixture"); await Assert.ThrowsAsync<InvalidOperationException>(() => Git.EnsureSafe(repo.Root, false)); }
    [Fact] public async Task DetectsRebaseDirectory() { using var repo = new TemporaryGitRepository(); Directory.CreateDirectory(Path.Combine(repo.Root, ".git/rebase-merge")); Assert.Contains("rebase-merge", (await Git.State(repo.Root)).Operations); }
    [Fact] public async Task FindsStagedUnstagedUntrackedAndDeleted() { using var repo = new TemporaryGitRepository(); repo.Write("deleted.cs", "a"); repo.Write("space name.cs", "a"); repo.Commit(); File.Delete(Path.Combine(repo.Root, "deleted.cs")); repo.Write("space name.cs", "b"); repo.Write("new.cs", "a"); repo.Run("add", "new.cs"); repo.Write("untracked.cs", "a"); Assert.Equal(new[] { "deleted.cs", "new.cs", "space name.cs", "untracked.cs" }, await Git.Changed(repo.Root)); }
    [Fact] public async Task RenameReportsBothPaths() { using var repo = new TemporaryGitRepository(); repo.Write("old.cs", "same"); repo.Commit(); repo.Run("mv", "old.cs", "new.cs"); var files = await Git.Changed(repo.Root); Assert.Contains("old.cs", files); Assert.Contains("new.cs", files); }
    [Fact] public async Task BaseIncludesCommittedAndLocalChanges() { using var repo = new TemporaryGitRepository(); var baseline = repo.Run("rev-parse", "HEAD").Trim(); repo.Write("committed.cs", "x"); repo.Commit(); repo.Write("local.cs", "x"); Assert.Equal(new[] { "committed.cs", "local.cs" }, await Git.Changed(repo.Root, baseline)); }
    [Fact] public async Task LinkedWorktreeOperationIsDetected() { using var repo = new TemporaryGitRepository(); var linked = Path.Combine(repo.Root, "linked"); repo.Run("worktree", "add", "-b", "other", linked); var gitDir = (await Git.Require(linked, "rev-parse", "--absolute-git-dir")).Trim(); File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), "fixture"); Assert.Contains("CHERRY_PICK_HEAD", (await Git.State(linked)).Operations); }
}
