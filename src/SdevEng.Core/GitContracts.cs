namespace SdevEng;

public static class GitOwnershipMarkers
{
    // Stored in the repository's local Git config under branch.<name>.
    public const string BranchConfigKey = "sdeveng-owned";
    public const string BranchConfigValue = "true";

    // Stored in the linked worktree's private Git directory, never its checkout.
    public const string WorktreeFileName = "sdeveng-owned-worktree";
    public const string WorktreeFileContents = "sdeveng-owned-worktree-v1\n";
}

public interface IStartWorkGitProcess
{
    Task<ProcessResult> TryEmptyBootstrapCommit(string root);
    Task<ProcessResult> StageBootstrapMarker(string root, string relativePath);
    Task<ProcessResult> CommitBootstrapMarker(string root, string relativePath);
    Task<ProcessResult> StageBootstrapRemoval(string root, string relativePath);
    Task<ProcessResult> CommitBootstrapRemoval(string root, string relativePath);
}
