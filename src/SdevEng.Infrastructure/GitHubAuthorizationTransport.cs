namespace SdevEng;

/// <summary>Low-level read and dry-run probes for GitHub operation capabilities.</summary>
public static class GitHubAuthorizationTransport
{
    public static Task<ProcessResult> ReadOrigin(string root) => Processes.Run("git", ["remote", "get-url", "--all", "origin"], root);
    public static Task<ProcessResult> ReadShortHead(string root) => Processes.Run("git", ["rev-parse", "--short=12", "HEAD"], root);
    public static Task<ProcessResult> DryRunPush(string root, string branchRef) =>
        Processes.Run("git", ["push", "--dry-run", "--porcelain", "origin", "HEAD:" + branchRef], root);
    public static Task<ProcessResult> AuthStatus(string root) => Processes.Run("gh", ["auth", "status"], root);
    public static Task<ProcessResult> ReadIssues(string root, string repository) =>
        Processes.Run("gh", ["api", "--method", "GET", $"repos/{repository}/issues?state=all&per_page=1"], root);
    public static Task<ProcessResult> ReadRepository(string root, string repository) =>
        Processes.Run("gh", ["api", "--method", "GET", $"repos/{repository}"], root);
    public static Task<ProcessResult> ReadWorkflowRuns(string root, string repository) =>
        Processes.Run("gh", ["api", "--method", "GET", $"repos/{repository}/actions/runs?per_page=1"], root);
}
