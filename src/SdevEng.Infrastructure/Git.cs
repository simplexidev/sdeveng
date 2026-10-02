using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

public static class Git
{
    public static async Task<string> RepositoryRoot(string root) => Path.GetFullPath((await Require(root, "rev-parse", "--show-toplevel")).Trim());
    public static async Task<string[]> TrackedWithIgnoreRules(string root) =>
        (await Require(root, "ls-files", "--cached", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static async Task<string> Head(string root) => (await Require(root, "rev-parse", "HEAD")).Trim();
    public static async Task<string> ResolveCommit(string root, string reference) =>
        (await Require(root, "rev-parse", "--verify", "--end-of-options", reference + "^{commit}")).Trim();
    public static async Task ValidateBranch(string root, string branch) =>
        _ = await Require(root, "check-ref-format", "--branch", branch);
    public static async Task<string> CurrentBranch(string root) => (await Require(root, "branch", "--show-current")).Trim();
    public static async Task<string[]> RemoteUrls(string root, string remote) =>
        (await Require(root, "remote", "get-url", "--all", remote)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static async Task<string?> TrySingleRemoteUrl(string root, string remote, TimeSpan timeout)
    {
        var result = await Processes.Run("git", ["remote", "get-url", "--all", remote], root, timeout: timeout);
        if (result.ExitCode != 0) return null;
        var urls = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        return urls.Length == 1 ? urls[0] : null;
    }
    public static async Task<string> RemoteUrl(string root, string remote) => (await RemoteUrls(root, remote)).Single();
    public static async Task<string> PushUrl(string root, string remote) => (await Require(root, "remote", "get-url", "--push", remote)).Trim();
    public static async Task<string?> BranchSha(string root, string branch)
    {
        await ValidateBranch(root, branch);
        var result = await Processes.Run("git", ["rev-parse", "--verify", "refs/heads/" + branch], root);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }
    public static async Task<int> CommitCount(string root, string from, string to) =>
        int.Parse((await Require(root, "rev-list", "--count", from + ".." + to)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    public static async Task<bool> IsAncestor(string root, string ancestor, string descendant) =>
        (await Processes.Run("git", ["merge-base", "--is-ancestor", ancestor, descendant], root)).ExitCode == 0;
    public static async Task<bool> IsOwnedBranch(string root, string branch)
    {
        var result = await Processes.Run("git", ["config", "--get", "branch." + branch + "." + GitOwnershipMarkers.BranchConfigKey], root);
        return result.ExitCode == 0 && result.Output.Trim() == GitOwnershipMarkers.BranchConfigValue;
    }
    public static async Task<bool> HasUserChangesOutsideRunStore(string root) =>
        (await Require(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".", ":(exclude).sdeveng/runs")).Length != 0;
    public static async Task CreateOwnedBranch(string root, string branch, string baseSha)
    {
        await ValidateBranch(root, branch);
        if (await BranchSha(root, branch) is not null) throw new InvalidOperationException("Named branch already exists.");
        if ((await State(root)).Operations.Count != 0 || await HasUserChangesOutsideRunStore(root))
            throw new InvalidOperationException("Worktree is busy or has unrelated changes.");
        await Require(root, "branch", branch, baseSha);
        await Require(root, "config", "branch." + branch + "." + GitOwnershipMarkers.BranchConfigKey, GitOwnershipMarkers.BranchConfigValue);
    }
    public static async Task CheckoutOwnedBranch(string root, string branch)
    {
        if (!await IsOwnedBranch(root, branch)) throw new InvalidOperationException("Branch is not owned by sdeveng.");
        if ((await State(root)).Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected.");
        await Require(root, "checkout", branch);
    }
    public static async Task<string?> RemoteBranchSha(string root, string remote, string branch)
    {
        await ValidateBranch(root, branch);
        var url = await PushUrl(root, remote);
        var result = await Processes.Run("git", ["ls-remote", "--heads", url, "refs/heads/" + branch], root);
        if (result.ExitCode != 0) throw new IOException("Remote branch inspection failed.");
        return result.Output.Split('\t')[0].Trim() is { Length: > 0 } sha ? sha : null;
    }
    public static async Task<ProcessResult> PushOwnedBranch(string root, string remote, string branch)
    {
        await ValidateBranch(root, branch);
        if (!await IsOwnedBranch(root, branch) || await BranchSha(root, branch) is null)
            throw new InvalidOperationException("Named local branch is absent or not owned.");
        if ((await State(root)).Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected.");
        return await Processes.Run("git", ["push", "--", remote, "refs/heads/" + branch + ":refs/heads/" + branch], root);
    }
    public static async Task<ProcessResult> PushNamedBranch(string root, string remote, string branch)
    {
        await ValidateBranch(root, branch);
        if (await BranchSha(root, branch) is null) throw new InvalidOperationException("Named local branch does not exist; no push performed.");
        _ = await Require(root, "remote", "get-url", remote);
        if ((await State(root)).Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no push performed.");
        return await Processes.Run("git", ["push", "--", remote, "refs/heads/" + branch + ":refs/heads/" + branch], root);
    }
    public static async Task<ProcessResult> WhitespaceCheck(string root)
    {
        var worktree = await Processes.Run("git", ["diff", "--check"], root);
        var index = await Processes.Run("git", ["diff", "--cached", "--check"], root);
        return new(worktree.ExitCode != 0 || index.ExitCode != 0 ? 1 : 0, worktree.Output + index.Output);
    }
    public static async Task SwitchCreateBranch(string root, string branch)
    {
        await EnsureSafe(root, true);
        await ValidateBranch(root, branch);
        await Require(root, "switch", "-c", branch);
    }
    public static async Task CreateWorktree(string root, string branch, string path)
    {
        await EnsureSafe(root, true);
        await ValidateBranch(root, branch);
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("Worktree path already exists; no worktree created.");
        await Require(root, "worktree", "add", "-b", branch, path);
    }
    public static async Task RemoveOwnedWorktree(string root, string path)
    {
        var repositoryRoot = (await State(root)).Root;
        if (SamePath(path, repositoryRoot)) throw new InvalidOperationException("Primary repository worktree cannot be removed.");
        if (!await IsRegisteredWorktree(root, path) || !Directory.Exists(path)) throw new InvalidOperationException("Path is not a registered linked worktree; nothing removed.");
        var state = await State(path);
        if (!SamePath(state.Root, path)) throw new InvalidOperationException("Path does not resolve to the named worktree; nothing removed.");
        if (!state.Clean) throw new InvalidOperationException("Linked worktree is dirty; nothing removed.");
        if (state.Operations.Count != 0) throw new InvalidOperationException("Linked worktree has an unfinished Git operation; nothing removed.");
        await Require(root, "worktree", "remove", path);
    }
    public static async Task AbandonOwnedWorktree(string root, string branch, string path)
    {
        await ValidateBranch(root, branch);
        var primary = (await State(root)).Root;
        if (SamePath(primary, path)) throw new InvalidOperationException("Primary worktree cannot be abandoned.");
        if (!await IsRegisteredWorktree(root, path) || !Directory.Exists(path)) throw new InvalidOperationException("Named linked worktree is unavailable.");
        var state = await State(path);
        if (!SamePath(state.Root, path) || state.Branch != branch || !state.Clean || state.Operations.Count != 0)
            throw new InvalidOperationException("Named worktree is dirty, busy, or on another branch.");
        var markerPath = Path.Combine((await Require(path, "rev-parse", "--absolute-git-dir")).Trim(), GitOwnershipMarkers.WorktreeFileName);
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != GitOwnershipMarkers.WorktreeFileContents || !await IsOwnedBranch(root, branch))
            throw new InvalidOperationException("Worktree and branch ownership markers are required.");
        if (!await IsAncestor(root, "refs/heads/" + branch, "HEAD"))
            throw new InvalidOperationException("Owned branch contains commits not merged into the primary HEAD; nothing removed.");
        await Require(root, "worktree", "remove", path);
        await Require(root, "branch", "-d", branch);
    }
    private static async Task<bool> IsRegisteredWorktree(string root, string path) =>
        (await Require(root, "worktree", "list", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line[9..].TrimEnd('\r')).Any(candidate => SamePath(candidate, path));
    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static async Task<string[]> ReadOwnedPaths(string root, string file)
    {
        var actual = Path.GetFullPath((await Require(root, "rev-parse", "--show-toplevel")).Trim());
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Paths file must contain a JSON array.");
        var paths = new List<string>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new InvalidOperationException("Every owned path must be a non-empty string.");
            var raw = item.GetString()!;
            if (Path.IsPathRooted(raw) || raw.StartsWith('/') || raw.Contains('\\')) throw new InvalidOperationException($"Owned path must be repository-relative with '/' separators: {raw}");
            var segments = raw.Split('/');
            if (segments.Any(segment => segment is "" or "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Unsafe owned path: {raw}");
            var full = Path.GetFullPath(Path.Combine(actual, Path.Combine(segments)));
            if (!full.StartsWith(actual + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException($"Owned path resolves outside repository: {raw}");
            var current = actual;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"Owned path traverses a symbolic link: {raw}");
            }
            paths.Add(string.Join('/', segments));
        }
        if (paths.Count == 0 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count) throw new InvalidOperationException("Paths file must contain a non-empty array of unique paths.");
        var changed = (await Changed(actual)).ToHashSet(StringComparer.Ordinal);
        var unavailable = paths.Where(path => !changed.Contains(path)).ToArray();
        if (unavailable.Length > 0) throw new InvalidOperationException("Owned paths are not currently changed, deleted, or untracked: " + string.Join(", ", unavailable));
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<object> StageOwned(string root, string pathsFile)
    {
        var paths = await ReadOwnedPaths(root, pathsFile);
        var unrelated = (await Changed(root)).Except(paths, StringComparer.Ordinal).ToArray();
        if (unrelated.Length > 0) throw new InvalidOperationException("Unrelated workspace changes detected; no paths staged: " + string.Join(", ", unrelated));
        await Require(root, new[] { "add", "--" }.Concat(paths.Select(path => ":(literal)" + path)).ToArray());
        return new { paths, count = paths.Length };
    }
    public static async Task<object> CommitOwned(string root, string pathsFile, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Commit message must be non-empty.");
        var paths = await ReadOwnedPaths(root, pathsFile);
        var state = await State(root);
        if (state.Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no commit performed.");
        var staged = (await ChangedPaths(root, staged: true)).Paths.Order(StringComparer.Ordinal).ToArray();
        if (!paths.SequenceEqual(staged, StringComparer.Ordinal)) throw new InvalidOperationException("Staged path set must exactly match the owned path set; no commit performed.");
        await Require(root, "commit", "-m", message);
        var sha = (await Require(root, "rev-parse", "HEAD")).Trim();
        var committed = (await Require(root, "diff-tree", "--root", "--no-commit-id", "--name-only", "--no-renames", "-r", "-z", "HEAD")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        if (!paths.SequenceEqual(committed, StringComparer.Ordinal)) throw new InvalidOperationException("Committed path set did not match the owned path set.");
        return new { sha, paths = committed, count = committed.Length };
    }
    public static async Task<string> Require(string root, params string[] args)
    {
        var r = await Processes.Run("git", args, root);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Git {args[0]} failed: {Secrets.Redact(r.Output.Trim())}");
        return r.Output;
    }
    public static async Task<GitState> State(string root)
    {
        var actual = Path.GetFullPath((await Require(root, "rev-parse", "--show-toplevel")).Trim());
        var operations = new List<string>();
        foreach (var op in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG", "sequencer", "index.lock" })
        {
            var path = (await Require(root, "rev-parse", "--git-path", op)).Trim();
            path = Path.GetFullPath(path, root);
            if (File.Exists(path) || Directory.Exists(path)) operations.Add(op);
        }
        var status = await Require(actual, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var branch = await Processes.Run("git", ["symbolic-ref", "--quiet", "--short", "HEAD"], root);
        var head = (await Require(actual, "rev-parse", "HEAD")).Trim();
        var remotes = new List<(string name, string url, string direction)>();
        var configuredRemotes = await Processes.Run("git", ["config", "--null", "--get-regexp", "^remote\\..*\\.(url|pushurl)$"], actual);
        if (configuredRemotes.ExitCode is 0 or 1)
        {
            var values = configuredRemotes.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < values.Length; i++)
            {
                var separator = values[i].IndexOf('\n');
                if (separator <= 0) continue;
                var key = values[i][..separator];
                var url = values[i][(separator + 1)..];
                var prefix = "remote.";
                var suffix = key.EndsWith(".pushurl", StringComparison.Ordinal) ? ".pushurl" : ".url";
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var name = key[prefix.Length..^suffix.Length];
                if (name.Length == 0) continue;
                var direction = suffix == ".pushurl" ? "(push)" : "(fetch)";
                remotes.Add((name, url, direction));
            }
        }
        var remoteNames = remotes.Select(remote => remote.name).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in remoteNames)
        {
            var hasPushUrl = remotes.Any(remote => remote.name == name && remote.direction == "(push)");
            if (!hasPushUrl)
                remotes.AddRange(remotes.Where(remote => remote.name == name && remote.direction == "(fetch)").Select(remote => (remote.name, remote.url, "(push)")).ToArray());
        }
        var upstream = await ReadUpstream(actual);
        return new(actual, head, branch.ExitCode == 0 ? branch.Output.Trim() : null, status.Length == 0, operations, status.Split('\0', StringSplitOptions.RemoveEmptyEntries), remotes.ToArray(), upstream);
    }
    private static async Task<GitUpstream?> ReadUpstream(string root)
    {
        var upstream = await Processes.Run("git", ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], root);
        if (upstream.ExitCode != 0) return null;
        var name = upstream.Output.Trim();
        var divergence = await Divergence(root, "HEAD", name);
        return new(name, divergence?.Ahead, divergence?.Behind);
    }
    public static async Task EnsureSafe(string root, bool requireClean)
    {
        var state = await State(root);
        if (state.Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no mutation performed.");
        if (state.Branch is null) throw new InvalidOperationException("Detached HEAD; no mutation performed.");
        if (requireClean && !state.Clean) throw new InvalidOperationException("Working tree has changes; preserve them before starting an issue.");
    }
    public static async Task<string[]> Files(string root) => (await Require(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static async Task<string[]> Tracked(string root) => (await Require(root, "ls-files", "--cached", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static async Task<string[]> Changed(string root, string? baseRef = null)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (baseRef is not null)
        {
            var sha = (await Require(root, "rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}")).Trim();
            var ancestor = await MergeBase(root, sha, "HEAD");
            names.UnionWith((await ChangedPaths(root, ancestor.Commit, staged: false)).Paths);
        }
        else
        {
            names.UnionWith((await ChangedPaths(root, staged: false)).Paths);
            names.UnionWith((await ChangedPaths(root, staged: true)).Paths);
        }
        names.UnionWith((await UntrackedPaths(root)).Paths);
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<GitChangedPaths> ChangedPaths(string root, string? from = null, bool staged = false)
    {
        var args = new List<string> { "diff" };
        if (staged) args.Add("--cached");
        args.AddRange(["--name-only", "--no-renames", "-z"]);
        if (from is not null) args.Add(from);
        args.Add("--");
        var paths = (await Require(root, args.ToArray())).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(paths);
    }
    public static async Task<GitChangedPaths> UntrackedPaths(string root)
    {
        var paths = (await Require(root, "ls-files", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(paths);
    }
    public static async Task<GitMergeBase> MergeBase(string root, string left, string right) =>
        new((await Require(root, "merge-base", left, right)).Trim());
    public static async Task<GitDivergence?> Divergence(string root, string left, string right)
    {
        var result = await Processes.Run("git", ["rev-list", "--left-right", "--count", $"{left}...{right}"], root);
        if (result.ExitCode != 0) return null;
        var counts = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return counts.Length == 2 && int.TryParse(counts[0], out var ahead) && int.TryParse(counts[1], out var behind)
            ? new(ahead, behind) : null;
    }
    public static async Task<object> ConflictForecast(string root, string baseRef, OutputSettings limits)
    {
        var target = (await Require(root, "rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}")).Trim();
        var head = (await Require(root, "rev-parse", "HEAD^{commit}")).Trim();
        var mergeBase = (await MergeBase(root, head, target)).Commit;
        var result = await Processes.Run("git", ["merge-tree", "--write-tree", "--name-only", "--messages", head, target], root);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("Git merge-tree could not forecast conflicts: " + Secrets.Redact(string.Join(' ', result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(limits.MaxLines))));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var conflicts = lines.Where(line => line.StartsWith("CONFLICT ", StringComparison.Ordinal)).ToArray();
        var paths = conflicts.Select(line => Regex.Match(line, @"(?: in |delete/modify: )(.+?)(?: deleted|$)").Groups[1].Value.Trim()).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).Take(limits.MaxItems).ToArray();
        return new { schemaVersion = 1, kind = "git-conflict-forecast", baseRef, head, target, mergeBase, hasConflicts = result.ExitCode == 1, conflictCount = conflicts.Length, paths, pathsTruncated = conflicts.Length > paths.Length, evidence = conflicts.Take(limits.MaxItems), note = "Forecast only: refs, index and worktree were not changed. Rename and custom merge-driver behavior may differ in a real merge." };
    }
}

public sealed class StartWorkGitProcess : IStartWorkGitProcess
{
    public Task<ProcessResult> TryEmptyBootstrapCommit(string root) =>
        Processes.Run("git", ["-c", "user.name=sdeveng", "-c", "user.email=sdeveng@localhost", "commit", "--allow-empty", "-m", "chore: initialize sdeveng work"], root);

    public Task<ProcessResult> StageBootstrapMarker(string root, string relativePath) =>
        Processes.Run("git", ["add", "-f", "--", relativePath], root);

    public Task<ProcessResult> CommitBootstrapMarker(string root, string relativePath) =>
        Processes.Run("git", ["-c", "user.name=sdeveng", "-c", "user.email=sdeveng@localhost", "commit", "--only", "-m", "chore: initialize sdeveng work", "--", relativePath], root);

    public Task<ProcessResult> StageBootstrapRemoval(string root, string relativePath) =>
        Processes.Run("git", ["add", "-u", "--", relativePath], root);

    public Task<ProcessResult> CommitBootstrapRemoval(string root, string relativePath) =>
        Processes.Run("git", ["-c", "user.name=sdeveng", "-c", "user.email=sdeveng@localhost", "commit", "--only", "-m", "chore: remove bootstrap marker", "--", relativePath], root);
}
