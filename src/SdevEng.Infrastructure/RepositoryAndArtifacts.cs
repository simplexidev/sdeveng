using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SdevEng;

public static class Repository
{
    public static async Task<RepositoryFileCatalog> FileCatalog(string root)
    {
        var repositoryRoot = Path.GetFullPath(root);
        var files = (await Git.Files(repositoryRoot)).Where(path => !SafeFiles.IsDiscoveryExcluded(path)).ToArray();
        var terms = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, path));
            if (!fullPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(fullPath)) continue;
            var info = new FileInfo(fullPath);
            if (info.Length is 0 or > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var bytes = await File.ReadAllBytesAsync(fullPath);
            if (bytes.Contains((byte)0)) continue;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { continue; }
            terms[path] = Regex.Matches(text, @"[\p{L}\p{N}_-]{2,}", RegexOptions.CultureInvariant)
                .Select(match => match.Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(256).ToArray();
        }
        return RepositoryFileCatalog.Create(files, terms);
    }

    public static async Task<object> Describe(string root, OutputSettings limits)
    {
        var repositoryRoot = await Git.RepositoryRoot(root);
        var tracked = await Git.TrackedWithIgnoreRules(repositoryRoot);
        var repositoryConfigurationFiles = tracked.Where(path => path.Split('/', 2)[0] is "AGENTS.md" or "Directory.Build.props" or "Directory.Build.targets" or "global.json" or "NuGet.Config" or "nuget.config" or "package.json" or "pnpm-workspace.yaml" or "pyproject.toml" or "Cargo.toml" or "go.mod" or "Makefile" or "justfile" or ".sdeveng")
            .ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tracked)))).ToLowerInvariant();
        var projects = new List<object>();
        var graphEdges = new List<ProjectDependencyEdge>();
        foreach (var path in Projects.Discover(repositoryRoot))
        {
            var evaluation = await Projects.Evaluate(repositoryRoot, path);
            var properties = evaluation["Properties"]!;
            var items = evaluation["Items"];
            var references = items?["ProjectReference"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>()
                .Select(reference => Path.GetRelativePath(repositoryRoot, reference).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray() ?? [];
            graphEdges.AddRange(references.Select(reference => new ProjectDependencyEdge(Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'), reference)));
            var packages = items?["PackageReference"]?.AsArray().Select(item => new { id = item?["Identity"]?.GetValue<string>(), version = item?["Version"]?.GetValue<string>() ?? item?["Metadata"]?["Version"]?.GetValue<string>() })
                .OrderBy(item => item.id, StringComparer.Ordinal).ToArray() ?? [];
            var targetFrameworks = (properties["TargetFrameworks"]?.GetValue<string>() is { Length: > 0 } multi ? multi : properties["TargetFramework"]?.GetValue<string>() ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToArray();
            var ownedFiles = items?["Compile"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>()
                .Select(file => Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
            var isTest = string.Equals(properties["IsTestProject"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase);
            var outputType = properties["OutputType"]?.GetValue<string>();
            var kind = isTest ? "test" : outputType is "Exe" or "WinExe" ? "application" : "library";
            projects.Add(new
            {
                path = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                language = Path.GetExtension(path) switch { ".fsproj" => "F#", ".vbproj" => "Visual Basic", _ => "C#" },
                kind,
                targetFrameworks,
                ownedFiles,
                projectReferences = references,
                packageReferences = packages
            });
        }
        return new
        {
            schemaVersion = 1,
            kind = "repository-description",
            root = repositoryRoot,
            repositoryConfigurationFiles = repositoryConfigurationFiles.Take(limits.MaxItems),
            repositoryConfigurationFileCount = repositoryConfigurationFiles.Length,
            repositoryConfigurationFilesTruncated = repositoryConfigurationFiles.Length > limits.MaxItems,
            catalogFingerprint = fingerprint,
            projects = projects.Take(limits.MaxItems),
            graph = DescribeGraph(Projects.Discover(repositoryRoot), repositoryRoot, graphEdges),
            projectCount = projects.Count,
            projectsTruncated = projects.Count > limits.MaxItems,
            trackedFiles = tracked.Take(limits.MaxItems),
            trackedFileCount = tracked.Length,
            trackedFilesTruncated = tracked.Length > limits.MaxItems
        };
    }

    private static object DescribeGraph(string[] projects, string root, List<ProjectDependencyEdge> edges)
    {
        var graph = new ProjectDependencyGraph(projects.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray(), edges.OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToArray());
        return new { graph.Nodes, graph.Edges, validation = graph.Validate() };
    }

    public static async Task<object> Summary(string root, string? baseRef, OutputSettings limits)
    {
        var state = await Git.State(root);
        var changed = await Git.Changed(root, baseRef);
        var upstream = state.Upstream;
        var byExtension = changed.GroupBy(path => Path.GetExtension(path).ToLowerInvariant() is { Length: > 0 } extension ? extension : "(none)", StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var byArea = changed.GroupBy(path => path.Replace('\\', '/').Split('/', 2)[0], StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new
        {
            schemaVersion = 1,
            kind = "repository-summary",
            state = new { state.Branch, state.Clean, state.Operations, entries = state.Entries.Take(limits.MaxItems), entriesTruncated = state.Entries.Length > limits.MaxItems },
            upstream = new { name = upstream?.Name, ahead = upstream?.Ahead, behind = upstream?.Behind },
            changes = new { baseRef, count = changed.Length, files = changed.Take(limits.MaxItems), truncated = changed.Length > limits.MaxItems, byExtension, byArea }
        };
    }
    public static async Task<object> Hygiene(string root, OutputSettings limits)
    {
        var tracked = await Git.Tracked(root); var candidates = new List<object>(); int total = 0;
        foreach (var relative in tracked)
        {
            var normalized = relative.Replace('\\', '/'); var file = Path.Combine(root, relative); string? reason = null;
            if (Regex.IsMatch(normalized, @"(^|/)(tests?|specs?)/fixtures/", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(normalized, @"(^|/)(bin|obj|dist|coverage|TestResults)/", RegexOptions.IgnoreCase)) reason = "tracked-output-directory";
            else if (Regex.IsMatch(normalized, @"(^|/)(\.DS_Store|Thumbs\.db)$|\.(tmp|bak|orig|rej|log)$", RegexOptions.IgnoreCase)) reason = "temporary-or-tool-artifact";
            else if (File.Exists(file) && new FileInfo(file).Length <= 1_000_000)
            {
                using var reader = new StreamReader(file); var prefix = new char[2048]; var read = reader.Read(prefix, 0, prefix.Length);
                var header = string.Join('\n', new string(prefix, 0, read).Split('\n').Take(5));
                if (Regex.IsMatch(header, @"(?im)^\s*(?://+|#+|/\*+|<!--)\s*(?:<auto-generated|auto[- ]generated|generated (?:code|file)|this file (?:is|was) generated|do not edit)")) reason = "generated-content-marker";
            }
            if (reason is null) continue; total++;
            if (candidates.Count < limits.MaxItems) candidates.Add(new { path = normalized, reason });
        }
        return new { schemaVersion = 1, kind = "repository-hygiene", trackedFiles = tracked.Length, candidateCount = total, candidates, truncated = total > candidates.Count, policy = "Evidence-backed candidates only; no file is deleted and reachability is not inferred." };
    }
}

public static class GitHub
{
    public static async Task<Result> Actions(GitHubActionsReader reader, string owner, string repository, string artifacts, string? runId, bool failedLogs, OutputSettings limits, CancellationToken cancellationToken = default)
    {
        if (failedLogs && runId is null) throw new ArgumentException("--failed-logs requires --run-id.");
        if (runId is not null && (!long.TryParse(runId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)) throw new ArgumentException("--run-id must be a positive integer.");
        var (ownerName, repositoryName) = (owner, repository);
        if (failedLogs)
        {
            var logs = await reader.ReadFailedLogsAsync(ownerName, repositoryName, long.Parse(runId!, CultureInfo.InvariantCulture), limits.MaxItems, limits.MaxOutputChars, cancellationToken);
            SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
            var path = Path.Combine(artifacts, $"actions-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, logs.Select(log => $"## {log.JobName ?? log.JobId.ToString(CultureInfo.InvariantCulture)}{Environment.NewLine}{log.Log}")), cancellationToken);
            return new("ok", new ProcessReport(0, Output.SummarizeFile(path, limits), path), 0);
        }
        if (runId is null)
        {
            var runs = await reader.ReadRunsAsync(ownerName, repositoryName, limits.MaxItems, cancellationToken);
            return Result.Ok(SummarizeActions(runs, null, limits));
        }
        var detail = await reader.ReadRunAsync(ownerName, repositoryName, long.Parse(runId, CultureInfo.InvariantCulture), 200, cancellationToken);
        return Result.Ok(SummarizeActions([detail.Run], detail.Jobs, limits));
    }

    static object SummarizeActions(IReadOnlyList<GitHubActionRun> sourceRuns, IReadOnlyList<GitHubActionJob>? sourceJobs, OutputSettings limits)
    {
        var runs = sourceRuns.Take(limits.MaxItems).Select(run => new { id = (object?)run.Id, workflow = run.Workflow, title = run.Title, status = run.Status, conclusion = run.Conclusion, @event = run.Event, branch = run.Branch, sha = run.Sha, url = run.Url.ToString(), createdAt = run.CreatedAt?.ToString("O", CultureInfo.InvariantCulture), updatedAt = run.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture) }).ToArray();
        var jobs = new List<object>(); var failedJobs = 0; var cancelledJobs = 0;
        foreach (var job in sourceJobs ?? [])
        {
            if (job.Conclusion is "failure" or "timed_out" or "action_required") failedJobs++;
            if (job.Conclusion == "cancelled") cancelledJobs++;
            if (jobs.Count >= limits.MaxItems) continue;
            var failedSteps = job.FailedSteps.Take(limits.MaxItems).Select(step => new { name = step.Name, number = (object?)step.Number, conclusion = step.Conclusion }).ToArray();
            jobs.Add(new { id = (object?)job.Id, name = job.Name, status = job.Status, conclusion = job.Conclusion, startedAt = job.StartedAt?.ToString("O", CultureInfo.InvariantCulture), completedAt = job.CompletedAt?.ToString("O", CultureInfo.InvariantCulture), url = job.Url?.ToString(), failedSteps });
        }
        var jobCount = sourceJobs?.Count ?? 0;
        return new { schemaVersion = 1, kind = "github-actions-summary", mode = sourceJobs is null ? "runs" : "run", runCount = sourceRuns.Count, runs, runsTruncated = sourceRuns.Count > runs.Length, jobCount, failedJobs, cancelledJobs, jobs, jobsTruncated = jobCount > jobs.Count, next = failedJobs > 0 ? "Fetch --failed-logs for this run and diagnose the earliest causal failure." : null };
    }

    public static object ParseActions(string json, bool list, OutputSettings limits)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var runs = list ? root.EnumerateArray().ToArray() : [root];
        var rows = runs.Take(limits.MaxItems).Select(run => new
        {
            id = Value(run, "databaseId"),
            workflow = Text(run, "workflowName") ?? Text(run, "name"),
            title = Text(run, "displayTitle"),
            status = Text(run, "status"),
            conclusion = Text(run, "conclusion"),
            @event = Text(run, "event"),
            branch = Text(run, "headBranch"),
            sha = Text(run, "headSha"),
            url = Text(run, "url"),
            createdAt = Text(run, "createdAt"),
            updatedAt = Text(run, "updatedAt")
        }).ToArray();
        var jobs = new List<object>(); int jobCount = 0, failedJobs = 0, cancelledJobs = 0;
        if (!list && root.TryGetProperty("jobs", out var jobArray) && jobArray.ValueKind == JsonValueKind.Array)
            foreach (var job in jobArray.EnumerateArray())
            {
                jobCount++; var conclusion = Text(job, "conclusion");
                if (conclusion is "failure" or "timed_out" or "action_required") failedJobs++;
                if (conclusion == "cancelled") cancelledJobs++;
                if (jobs.Count >= limits.MaxItems) continue;
                var failedSteps = job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                    ? steps.EnumerateArray().Where(step => Text(step, "conclusion") is "failure" or "cancelled" or "timed_out").Take(limits.MaxItems).Select(step => new { name = Text(step, "name"), number = Value(step, "number"), conclusion = Text(step, "conclusion") }).ToArray() : [];
                jobs.Add(new { id = Value(job, "databaseId"), name = Text(job, "name"), status = Text(job, "status"), conclusion, startedAt = Text(job, "startedAt"), completedAt = Text(job, "completedAt"), url = Text(job, "url"), failedSteps });
            }
        return new { schemaVersion = 1, kind = "github-actions-summary", mode = list ? "runs" : "run", runCount = runs.Length, runs = rows, runsTruncated = runs.Length > rows.Length, jobCount, failedJobs, cancelledJobs, jobs, jobsTruncated = jobCount > jobs.Count, next = failedJobs > 0 ? "Fetch --failed-logs for this run and diagnose the earliest causal failure." : null };
    }
    static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static object? Value(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.ValueKind switch { JsonValueKind.Number when value.TryGetInt64(out var number) => number, JsonValueKind.String => value.GetString(), _ => value.ToString() } : null;
}

public static class Results
{
    static readonly Dictionary<string, string> Persistent = new(StringComparer.Ordinal) { ["audit"] = "audits", ["handoff"] = "handoffs", ["review"] = "reviews", ["report"] = "reports" };
    static readonly string[] Directories = ["audits", "handoffs", "reviews", "reports", "evals", "logs", "traces", "sarif", "binlogs", "test-results", "tmp"];
    static readonly string[] Transient = ["evals/generated", "logs", "traces", "sarif", "binlogs", "test-results", "tmp"];
    static readonly Regex Name = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static string Root(string root) => Path.Combine(Path.GetFullPath(root), ".agent-results");
    static string Type(string value) => Persistent.TryGetValue(value, out var directory) ? directory : throw new ArgumentException("Result type must be audit, handoff, review, or report.");
    public static void RequireWords(string[] words, int count, string usage) { if (words.Length != count) throw new ArgumentException(usage); }
    static string SafeName(string name) => Name.IsMatch(name) && name.Length <= 80 ? name : throw new ArgumentException("Result name must be 1-80 lowercase letters, numbers, and single hyphens.");
    static string Readme => """
        # Agent results

        Durable handoffs, audits, reviews, and reports live here. Large or transient output belongs in the named transient directories and is ignored. Normal repository discovery excludes this directory; reference an artifact by path when it matters to a later independent chat.
        """;
    public static Result Init(string root)
    {
        var results = Root(root); SafeFiles.NoLinks(results); Directory.CreateDirectory(results);
        foreach (var directory in Directories) Directory.CreateDirectory(Path.Combine(results, directory));
        Directory.CreateDirectory(Path.Combine(results, "evals", "generated"));
        var readme = Path.Combine(results, "README.md"); var created = !File.Exists(readme);
        if (created) SafeFiles.Atomic(readme, Readme);
        return Result.Ok(new { initialized = results, directories = Directories, readmeCreated = created });
    }
    public static async Task<Result> New(string root, string[] words)
    {
        RequireWords(words, 2, "Usage: results new <audit|handoff|review|report> <name>.");
        var type = words[0]; var directory = Type(type); var name = SafeName(words[1]); Init(root); var now = DateTimeOffset.UtcNow;
        var state = await Git.State(root); var head = state.Head; var branch = state.Branch ?? "";
        var file = Path.Combine(Root(root), directory, $"{now:yyyyMMddTHHmmssZ}-{name}.md"); if (File.Exists(file)) throw new IOException("A result already exists for this timestamp and name; retry.");
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type) + ": " + name.Replace('-', ' ');
        SafeFiles.Atomic(file, $"""
            # {title}

            - Time (UTC): {now:O}
            - HEAD: {head}
            - Branch: {branch}
            - Status: draft

            ## Purpose

            ## Findings or summary

            ## Decisions

            ## Unresolved

            ## Follow-up

            ## Artifact paths
            """);
        return Result.Ok(new { path = file, type, name, timestampUtc = now, head, branch, status = "draft" });
    }
    static ResultFile[] Files(string root, string? type = null)
    {
        var results = Root(root); if (!Directory.Exists(results)) return [];
        var folders = type is null ? Persistent.Select(x => x.Value) : [Type(type)];
        return folders.SelectMany(folder => Directory.Exists(Path.Combine(results, folder)) ? Directory.EnumerateFiles(Path.Combine(results, folder), "*.md") : [])
            .Select(ResultFile.From).OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Path, StringComparer.Ordinal).ToArray();
    }
    public static Result List(string root, string[] words) { if (words.Length > 1) throw new ArgumentException("Usage: results list [audit|handoff|review|report]."); var files = Files(root, words.FirstOrDefault()); return Result.Ok(new { count = files.Length, results = files }); }
    public static Result Latest(string root, string[] words) { RequireWords(words, 1, "Usage: results latest <audit|handoff|review|report>."); return Result.Ok(new { result = Files(root, words[0]).FirstOrDefault() }); }
    public static Result Context(string root, string[] words) { RequireWords(words, 1, "Usage: results context <audit|handoff|review|report>."); var file = Files(root, words[0]).FirstOrDefault(); return Result.Ok(new { result = file is null ? null : new { file.Path, file.Type, file.TimestampUtc, file.Status, carryForward = file.CarryForward } }); }
    public static Result Clean(string root, bool dryRun)
    {
        var results = Root(root); if (!Directory.Exists(results)) return Result.Ok(new { dryRun, removed = 0, paths = Array.Empty<string>() }); SafeFiles.NoLinks(results); var paths = new List<string>();
        foreach (var directory in Transient) { var target = Path.Combine(results, directory); SafeFiles.NoLinks(target); if (Directory.Exists(target)) paths.AddRange(EnumerateTransient(target)); }
        if (!dryRun) foreach (var path in paths) File.Delete(path); return Result.Ok(new { dryRun, removed = paths.Count, paths });
    }
    static IEnumerable<string> EnumerateTransient(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) foreach (var file in EnumerateTransient(child)) yield return file;
    }
}
public record ResultFile(string Path, string Type, DateTimeOffset TimestampUtc, string? Status, string CarryForward)
{
    public static ResultFile From(string path)
    {
        var stamp = System.IO.Path.GetFileNameWithoutExtension(path).Split('-', 2)[0]; if (!DateTimeOffset.TryParseExact(stamp, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) timestamp = File.GetLastWriteTimeUtc(path);
        var text = File.ReadAllText(path); var statusMatch = Regex.Match(text, @"(?m)^- Status: (.+)$");
        var carry = string.Join("\n", Regex.Matches(text, @"(?ms)^## (?:Unresolved|Follow-up)\r?\n(.*?)(?=^## |\z)").SelectMany(x => x.Groups[1].Value.Split('\n')).Select(x => x.Trim()).Where(x => x.Length > 0).Take(8));
        var directory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)!); var type = new Dictionary<string, string> { ["audits"] = "audit", ["handoffs"] = "handoff", ["reviews"] = "review", ["reports"] = "report" }.GetValueOrDefault(directory, directory);
        return new(path, type, timestamp, statusMatch.Success ? statusMatch.Groups[1].Value : null, carry);
    }
}

public static class Projects
{
    private static readonly object WorkspaceRegistrationLock = new();
    private static bool workspaceRegistered;
    public static string[] Discover(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".csproj" or ".fsproj" or ".vbproj").Order(StringComparer.Ordinal).ToArray();
    public static string[] Solutions(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".sln" or ".slnx").Order(StringComparer.Ordinal).ToArray();
    public static async Task<SolutionWorkspaceModel> LoadSolution(string root, string solution)
    {
        var repositoryRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(solution, repositoryRoot);
        if (!File.Exists(fullPath) || Path.GetExtension(fullPath) is not (".sln" or ".slnx")) throw new ArgumentException("A solution file (.sln or .slnx) must exist.", nameof(solution));
        lock (WorkspaceRegistrationLock)
        {
            if (!workspaceRegistered)
            {
                if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
                workspaceRegistered = true;
            }
        }
        using var workspace = MSBuildWorkspace.Create();
        var diagnostics = new List<WorkspaceDiagnostic>();
        workspace.WorkspaceFailed += (_, args) => diagnostics.Add(NormalizeWorkspaceDiagnostic(args.Diagnostic, repositoryRoot));
        var loaded = await workspace.OpenSolutionAsync(fullPath);
        var loadedProjects = loaded.Projects.ToDictionary(project => Path.GetFullPath(project.FilePath ?? project.Name), StringComparer.Ordinal);
        foreach (var projectPath in await InSolutionProjectPaths(repositoryRoot, fullPath))
        {
            if (!loadedProjects.ContainsKey(projectPath))
            {
                try { loadedProjects[projectPath] = await workspace.OpenProjectAsync(projectPath); }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
                {
                    diagnostics.Add(new("PROJECT_FALLBACK", "Failure", Regex.Replace(exception.Message, @"\s+", " ").Trim(), Path.GetRelativePath(repositoryRoot, projectPath).Replace('\\', '/')));
                }
            }
        }
        var projects = loadedProjects.Keys.Order(StringComparer.Ordinal).ToArray();
        var compilable = new List<string>();
        foreach (var project in loadedProjects.Values.OrderBy(project => Path.GetFullPath(project.FilePath ?? project.Name), StringComparer.Ordinal))
        {
            if (await project.GetCompilationAsync() is not null) compilable.Add(Path.GetFullPath(project.FilePath ?? project.Name));
        }
        return new(Path.GetFullPath(fullPath), projects, compilable.Order(StringComparer.Ordinal).ToArray(), diagnostics.Distinct().OrderBy(item => item.ProjectPath, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal).ToArray());
    }

    public static async Task<SemanticSolutionModel> SemanticModel(string root, string solution)
    {
        var workspace = await LoadSolution(root, solution);
        if (workspace.Projects.Any(path => Path.GetExtension(path) != ".csproj"))
            throw new InvalidOperationException("Semantic models currently support C# projects only.");
        if (workspace.Diagnostics.Any(diagnostic => diagnostic.Kind == "Failure") || workspace.CompilationAvailableProjects.Length != workspace.Projects.Length)
            throw new InvalidOperationException("The solution is incomplete; semantic models require a compilation for every project.");

        var repositoryRoot = Path.GetFullPath(root);
        using var semanticWorkspace = MSBuildWorkspace.Create();
        var solutionModel = await semanticWorkspace.OpenSolutionAsync(Path.GetFullPath(solution, repositoryRoot));
        var projects = new List<SemanticProjectModel>();
        var edges = new List<SemanticProjectEdgeModel>();
        var projectCompilations = new List<(Microsoft.CodeAnalysis.Project Project, Compilation Compilation)>();
        foreach (var project in solutionModel.Projects.OrderBy(item => Path.GetFullPath(item.FilePath ?? item.Name), StringComparer.Ordinal))
        {
            if (project.Language != LanguageNames.CSharp)
                throw new InvalidOperationException("The solution contains a project without C# semantic support.");
            var compilation = await project.GetCompilationAsync();
            if (compilation is null || compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException($"Project {Path.GetFileName(project.FilePath)} has incomplete semantic state.");
            projectCompilations.Add((project, compilation));
        }
        foreach (var (project, compilation) in projectCompilations)
        {
            var sourceProject = Path.GetRelativePath(repositoryRoot, project.FilePath!).Replace('\\', '/');
            var evaluation = await Evaluate(repositoryRoot, project.FilePath!);
            var properties = evaluation["Properties"]!;
            var isTest = IsTestProperty(properties);
            var packageNames = evaluation["Items"]?["PackageReference"]?.AsArray().Select(item => item?["Identity"]?.GetValue<string>()).OfType<string>() ?? [];
            var testFramework = DotnetFacts.DetectTestProfile(repositoryRoot, properties, packageNames).Framework;
            var types = new List<SemanticTypeModel>();
            var namespaces = new List<SemanticNamespaceModel>();
            var relationships = new List<SemanticTypeRelationshipModel>();
            AddSymbols(compilation.Assembly.GlobalNamespace, namespaces, types, relationships);
            var references = new List<SemanticReferenceModel>();
            var callSites = new List<SemanticCallSiteModel>();
            foreach (var document in project.Documents.OrderBy(item => item.FilePath, StringComparer.Ordinal))
            {
                if (document.FilePath is null || await document.GetSyntaxRootAsync() is not { } syntax) continue;
                var model = await document.GetSemanticModelAsync();
                if (model is null) continue;
                foreach (var node in syntax.DescendantNodes())
                {
                    if (node is not (IdentifierNameSyntax or GenericNameSyntax or InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
                    var symbol = model.GetSymbolInfo(node).Symbol;
                    if (symbol is null) continue;
                    var caller = model.GetEnclosingSymbol(node.SpanStart);
                    var callerKey = caller is null ? null : StableKey(caller);
                    var line = node.GetLocation().GetLineSpan();
                    var location = $"{Path.GetRelativePath(repositoryRoot, line.Path).Replace('\\', '/')}:{line.StartLinePosition.Line + 1}:{line.StartLinePosition.Character + 1}";
                    var targetAssembly = symbol.ContainingAssembly;
                    var targetProject = projectCompilations.FirstOrDefault(item =>
                        !ReferenceEquals(item.Project, project) && targetAssembly is not null &&
                        SymbolEqualityComparer.Default.Equals(item.Compilation.Assembly, targetAssembly)).Project;
                    if (targetProject is not null)
                        edges.Add(new(sourceProject, Path.GetRelativePath(repositoryRoot, targetProject.FilePath!).Replace('\\', '/'),
                            callerKey ?? "", StableKey(symbol), node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax ? "call" : "reference", location));
                    if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
                        callSites.Add(new(StableKey(symbol), location, callerKey));
                    else references.Add(new(StableKey(symbol), location, callerKey));
                }
            }
            projects.Add(new(Path.GetRelativePath(repositoryRoot, project.FilePath!).Replace('\\', '/'),
                namespaces.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(), types.OrderBy(type => type.Name, StringComparer.Ordinal).ToArray())
            {
                IsTest = isTest,
                TestFramework = testFramework,
                TestMethods = isTest ? TestMethods(compilation.Assembly.GlobalNamespace, testFramework) : [],
                References = references.OrderBy(item => item.Location, StringComparer.Ordinal).ThenBy(item => item.TargetKey, StringComparer.Ordinal).ToArray(),
                CallSites = callSites.OrderBy(item => item.Location, StringComparer.Ordinal).ThenBy(item => item.TargetKey, StringComparer.Ordinal).ToArray(),
                TypeRelationships = relationships.OrderBy(item => item.SourceKey, StringComparer.Ordinal).ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.TargetKey, StringComparer.Ordinal).ToArray()
            });
        }
        return new(Path.GetFullPath(solution, repositoryRoot), projects.ToArray())
        {
            ProjectDependencies = projectCompilations.SelectMany(item => item.Project.ProjectReferences.Select(reference =>
                new SemanticProjectDependencyModel(Path.GetRelativePath(repositoryRoot, item.Project.FilePath!).Replace('\\', '/'),
                    Path.GetRelativePath(repositoryRoot, solutionModel.GetProject(reference.ProjectId)!.FilePath!).Replace('\\', '/'))))
                .Distinct().OrderBy(item => item.SourceProject, StringComparer.Ordinal).ThenBy(item => item.TargetProject, StringComparer.Ordinal).ToArray(),
            ProjectEdges = edges.Distinct().OrderBy(item => item.SourceProject, StringComparer.Ordinal)
                .ThenBy(item => item.TargetProject, StringComparer.Ordinal).ThenBy(item => item.Location, StringComparer.Ordinal)
                .ThenBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.TargetKey, StringComparer.Ordinal).ToArray()
        };
    }

    private static bool IsTestProperty(JsonNode properties) => string.Equals(properties["IsTestProject"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase);

    private static SemanticTestMethodModel[] TestMethods(INamespaceSymbol root, string framework)
    {
        var methods = new List<SemanticTestMethodModel>();
        void VisitType(INamedTypeSymbol type)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(item => item.MethodKind == MethodKind.Ordinary && !item.IsImplicitlyDeclared))
            {
                if (method.GetAttributes().Any(attribute => IsTestAttribute(attribute.AttributeClass, framework)))
                    methods.Add(new(StableKey(method), SymbolLocation(method), framework));
            }
            foreach (var nested in type.GetTypeMembers()) VisitType(nested);
        }
        void VisitNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers()) VisitType(type);
            foreach (var child in ns.GetNamespaceMembers()) VisitNamespace(child);
        }
        VisitNamespace(root);
        return methods.OrderBy(item => item.StableKey, StringComparer.Ordinal).ThenBy(item => item.Location, StringComparer.Ordinal).ToArray();
    }

    private static bool IsTestAttribute(INamedTypeSymbol? attribute, string framework)
    {
        for (var current = attribute; current is not null; current = current.BaseType)
        {
            var name = current.ToDisplayString();
            if (framework is "xunit-v2" or "xunit-v3" && name is ("Xunit.FactAttribute" or "Xunit.TheoryAttribute")) return true;
            if (framework == "nunit" && name is ("NUnit.Framework.TestAttribute" or "NUnit.Framework.TestCaseAttribute" or "NUnit.Framework.TestCaseSourceAttribute")) return true;
            if (framework == "mstest" && name is ("Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute" or "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute")) return true;
            if (framework == "tunit" && name == "TUnit.Core.TestAttribute") return true;
        }
        return false;
    }

    private static void AddSymbols(INamespaceSymbol ns, List<SemanticNamespaceModel> namespaces, List<SemanticTypeModel> result, List<SemanticTypeRelationshipModel> relationships)
    {
        if (!ns.IsGlobalNamespace) namespaces.Add(new(ns.ToDisplayString()));
        foreach (var child in ns.GetNamespaceMembers().OrderBy(item => item.Name, StringComparer.Ordinal)) AddSymbols(child, namespaces, result, relationships);
        foreach (var type in ns.GetTypeMembers().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            AddRelationships(type, relationships);
            result.Add(new(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type.TypeKind.ToString(), type.DeclaredAccessibility.ToString(),
                BaseTypes(type),
                type.GetMembers().Where(member => !member.IsImplicitlyDeclared).Select(member => member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal).ToArray(),
                Callables(type))
            { StableKey = StableKey(type), Location = SymbolLocation(type), DataMembers = DataMembers(type) });
            foreach (var nested in type.GetTypeMembers().OrderBy(item => item.Name, StringComparer.Ordinal)) AddNested(nested, result, relationships);
        }
    }
    private static void AddNested(INamedTypeSymbol type, List<SemanticTypeModel> result, List<SemanticTypeRelationshipModel> relationships)
    {
        AddRelationships(type, relationships);
        result.Add(new(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), type.TypeKind.ToString(), type.DeclaredAccessibility.ToString(),
            BaseTypes(type),
            type.GetMembers().Where(member => !member.IsImplicitlyDeclared).Select(member => member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal).ToArray(),
            Callables(type))
        { StableKey = StableKey(type), Location = SymbolLocation(type), DataMembers = DataMembers(type) });
        foreach (var nested in type.GetTypeMembers().OrderBy(item => item.Name, StringComparer.Ordinal)) AddNested(nested, result, relationships);
    }
    private static void AddRelationships(INamedTypeSymbol type, List<SemanticTypeRelationshipModel> relationships)
    {
        var source = StableKey(type);
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            relationships.Add(new(source, StableKey(baseType), "inherits"));
        foreach (var contract in type.Interfaces)
            relationships.Add(new(source, StableKey(contract), type.TypeKind == TypeKind.Interface ? "inherits" : "implements"));
    }
    private static SemanticCallableModel[] Callables(INamedTypeSymbol type) => type.GetMembers()
        .Where(member => !member.IsImplicitlyDeclared && member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor })
        .Select(member =>
        {
            var method = (IMethodSymbol)member;
            var location = method.Locations.Where(item => item.IsInSource).Select(item => item.GetLineSpan())
                .Where(span => span.IsValid).Select(span => $"{span.Path.Replace('\\', '/')}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}")
                .Order(StringComparer.Ordinal).FirstOrDefault() ?? "";
            return new SemanticCallableModel(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? "Constructor" : "Method",
                method.DeclaredAccessibility.ToString(), location)
            { StableKey = StableKey(method) };
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Location, StringComparer.Ordinal).ToArray();
    private static SemanticMemberModel[] DataMembers(INamedTypeSymbol type) => type.GetMembers()
        .Where(member => !member.IsImplicitlyDeclared && member is IPropertySymbol or IFieldSymbol or IEventSymbol)
        .Select(member =>
        {
            var location = member.Locations.Where(item => item.IsInSource).Select(item => item.GetLineSpan())
                .Where(span => span.IsValid).Select(span => $"{span.Path.Replace('\\', '/')}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}")
                .Order(StringComparer.Ordinal).FirstOrDefault() ?? "";
            var kind = member switch { IPropertySymbol => "Property", IFieldSymbol => "Field", IEventSymbol => "Event", _ => "" };
            return new SemanticMemberModel(member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), kind, member.DeclaredAccessibility.ToString(), location) { StableKey = StableKey(member) };
        }).OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Location, StringComparer.Ordinal).ToArray();
    private static string StableKey(ISymbol symbol) => DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string SymbolLocation(ISymbol symbol) => symbol.Locations.Where(item => item.IsInSource).Select(item => item.GetLineSpan())
        .Where(span => span.IsValid).Select(span => $"{span.Path.Replace('\\', '/')}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}")
        .Order(StringComparer.Ordinal).FirstOrDefault() ?? "";
    private static string[] BaseTypes(INamedTypeSymbol type) =>
        (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType ? new[] { baseType } : Enumerable.Empty<INamedTypeSymbol>())
        .Concat(type.Interfaces).Select(item => item.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Order(StringComparer.Ordinal).ToArray();

    private static async Task<string[]> InSolutionProjectPaths(string root, string solution)
    {
        var directory = Path.GetDirectoryName(solution)!;
        var result = await Processes.Run("dotnet", ["sln", solution, "list"], root);
        if (result.ExitCode != 0) return [];
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Path.GetExtension(line.Trim('"')) is ".csproj" or ".fsproj" or ".vbproj")
            .Select(line => Path.GetFullPath(line.Trim('"'), directory)).Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static WorkspaceDiagnostic NormalizeWorkspaceDiagnostic(Microsoft.CodeAnalysis.WorkspaceDiagnostic diagnostic, string root)
    {
        var message = Regex.Replace(diagnostic.Message.Replace('\\', '/'), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/') + "/";
        message = message.Replace(normalizedRoot, "", StringComparison.OrdinalIgnoreCase);
        return new("MSBUILD_WORKSPACE", diagnostic.Kind.ToString(), message, null);
    }
    public static async Task<string[]> InSolution(string root, string solution)
    {
        var full = Path.GetFullPath(solution, root); if (!File.Exists(full)) throw new ArgumentException("Solution does not exist.");
        var result = await Processes.Run("dotnet", ["sln", full, "list"], root);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not list projects in {Path.GetFileName(full)}.");
        var directory = Path.GetDirectoryName(full)!;
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Path.GetExtension(line.Trim('"')) is ".csproj" or ".fsproj" or ".vbproj")
            .Select(line => Path.GetFullPath(line.Trim('"'), directory)).Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<JsonNode> Evaluate(string root, string project)
    {
        var result = await Processes.Run("dotnet", ["msbuild", project, "-nologo", "-getProperty:TargetFramework,TargetFrameworks,OutputType,IsTestProject,Nullable,ManagePackageVersionsCentrally,Deterministic,EnableNETAnalyzers,RestorePackagesWithLockFile,EnablePackageValidation,PackageValidationBaselineVersion,IsTestingPlatformApplication,TestingPlatformDotnetTestSupport,UseMicrosoftTestingPlatformRunner", "-getItem:ProjectReference,Compile,PackageReference"], root);
        if (result.ExitCode != 0) throw new InvalidOperationException($"MSBuild evaluation failed for {Path.GetFileName(project)}; graph cannot safely be narrowed.");
        return JsonNode.Parse(result.Output) ?? throw new InvalidOperationException("Empty MSBuild response.");
    }
    public static async Task<ProjectDependencyGraph> DependencyGraph(string root)
    {
        root = Path.GetFullPath(root);
        var projects = Discover(root);
        var edges = new List<ProjectDependencyEdge>();
        foreach (var project in projects)
        {
            var items = (await Evaluate(root, project))["Items"];
            var dependencies = items?["ProjectReference"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>() ?? [];
            edges.AddRange(dependencies.Select(dependency => new ProjectDependencyEdge(project, Path.GetFullPath(dependency))));
        }
        return new(projects, edges.OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToArray());
    }
    public static string[] Dependents(ProjectDependencyGraph graph, string project)
    {
        var target = Path.GetFullPath(project);
        return graph.AffectedProjects([target]).Where(candidate => candidate != target).ToArray();
    }
    public static async Task<bool> IsTest(string root, string project) => (await Evaluate(root, project))["Properties"]?["IsTestProject"]?.GetValue<string>().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    public static async Task<Affected> Affected(string root, string[] changed)
    {
        root = Path.GetFullPath(root);
        changed = changed.Where(x => !SafeFiles.IsDiscoveryExcluded(x)).ToArray();
        var projects = Discover(root);
        if (changed.Length == 0) return new([], "No build-relevant changed files.");
        var broad = changed.Any(x => x.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x) is "global.json" or "NuGet.Config" or "nuget.config" || x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (broad) return new(projects, "Shared build, solution or project metadata changed; conservative full graph.");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var graph = await DependencyGraph(root);
        var paths = changed.Select(x => Path.GetFullPath(x, root)).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var evaluation = await Evaluate(root, project);
            if (!string.IsNullOrEmpty(evaluation["Properties"]?["TargetFrameworks"]?.GetValue<string>())) return new(projects, "Multi-targeted graph; conservative full graph (conditional inner builds may differ).");
            var items = evaluation["Items"];
            var compiles = items?["Compile"]?.AsArray().Select(x => x?["FullPath"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            if (compiles.Any(paths.Contains) || paths.Any(p => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))) selected.Add(project);
        }
        // Removed linked files and custom build inputs cannot always be inferred from evaluated Compile items.
        if (paths.Any(p => !projects.Any(project => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
            return new(projects, "Change outside project directories; conservative full graph for custom or removed linked inputs.");
        var affected = graph.AffectedProjects(selected);
        return new(affected, "Evaluated Compile/ProjectReference graph including transitive dependents.",
            affected.ToDictionary(project => project, project => graph.ExplanationPath(selected, project)!, StringComparer.Ordinal));
    }
    public static async Task<bool> HasApiChecks(string root, string path)
    {
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return false;
        var evaluation = await Evaluate(root, path);
        var properties = evaluation["Properties"];
        return string.Equals(properties?["EnablePackageValidation"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(properties?["PackageValidationBaselineVersion"]?.GetValue<string>())
            || evaluation["Items"]?["PackageReference"]?.AsArray().Any(item => string.Equals(item?["Identity"]?.GetValue<string>(), "Microsoft.CodeAnalysis.PublicApiAnalyzers", StringComparison.OrdinalIgnoreCase)) == true;
    }
    public static async Task<string[]> DependentTests(string root, string project)
    {
        var target = Path.GetFullPath(project);
        var graph = await DependencyGraph(root);
        var tests = new List<string>();
        foreach (var candidate in graph.Nodes) if (await IsTest(root, candidate)) tests.Add(candidate);
        return graph.AffectedTestProjects([target], tests).Where(candidate => candidate != target).ToArray();
    }
    public static async Task<object> Ownership(string root, string file)
    {
        root = Path.GetFullPath(root); var full = Path.GetFullPath(file, root); var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new ArgumentException("File must be inside the repository root.");
        var projects = Discover(root); var compileOwners = new List<string>(); var directoryOwners = new List<string>();
        foreach (var project in projects)
        {
            var evaluation = await Evaluate(root, project);
            var compiles = evaluation["Items"]?["Compile"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>() ?? [];
            if (compiles.Any(path => string.Equals(Path.GetFullPath(path), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) compileOwners.Add(project);
            var directory = Path.GetDirectoryName(project)!;
            if (full.StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) directoryOwners.Add(project);
        }
        var owners = compileOwners.Count > 0 ? compileOwners : directoryOwners.OrderByDescending(path => Path.GetDirectoryName(path)!.Length).Take(1).ToList();
        var impacted = new HashSet<string>(owners, StringComparer.Ordinal);
        foreach (var owner in owners) foreach (var dependent in await Dependents(root, owner)) impacted.Add(dependent);
        string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        return new { schemaVersion = 1, kind = "file-ownership", file = relative, exists = File.Exists(full), basis = compileOwners.Count > 0 ? "evaluated-compile-item" : owners.Count > 0 ? "nearest-project-directory" : "unowned", owners = owners.Select(Rel), impactedProjects = impacted.Order(StringComparer.Ordinal).Select(Rel) };
    }
    public static async Task<string[]> Dependents(string root, string project)
    {
        return Dependents(await DependencyGraph(root), project);
    }
    public static async Task<List<object>> Health(string root, HealthSettings policy)
    {
        var findings = new List<object>(); var projects = Discover(root);
        if (projects.Length == 0) findings.Add(new { rule = "projects", message = "No project files found; file-based apps are not project-scanned." });
        foreach (var project in projects)
        {
            var props = (await Evaluate(root, project))["Properties"]!;
            foreach (var (name, expected, enabled) in new[] { ("Nullable", "enable", policy.RequireNullable), ("ManagePackageVersionsCentrally", "true", policy.RequireCentralPackages), ("Deterministic", "true", policy.RequireDeterministic), ("EnableNETAnalyzers", "true", policy.RequireAnalyzers), ("RestorePackagesWithLockFile", "true", policy.RequireLockFiles) })
                if (enabled && !string.Equals(props[name]?.GetValue<string>(), expected, StringComparison.OrdinalIgnoreCase)) findings.Add(new { project, rule = name, expected, actual = props[name]?.GetValue<string>() });
            var frameworks = (props["TargetFrameworks"]?.GetValue<string>() is { Length: > 0 } multi ? multi : props["TargetFramework"]?.GetValue<string>() ?? "").Split(';');
            foreach (var tfm in frameworks) if (policy.AllowedFrameworks.Length > 0 && !policy.AllowedFrameworks.Contains(tfm)) findings.Add(new { project, rule = "TargetFramework", actual = tfm });
        }
        if (!File.Exists(Path.Combine(root, "global.json"))) findings.Add(new { rule = "sdk", message = "Missing global.json SDK policy." });
        return findings;
    }
}
public record Affected(string[] Projects, string Reason, IReadOnlyDictionary<string, string[]>? ExplanationPaths = null);

public static class DotnetFacts
{
    public static async Task<object> Inspect(string root, string? explicitProject)
    {
        root = Path.GetFullPath(root);
        var projects = explicitProject is null ? Projects.Discover(root) : IsSolution(explicitProject) ? await Projects.InSolution(root, explicitProject) : ResolveTargets(root, explicitProject);
        var sdkVersion = await Processes.Run("dotnet", ["--version"], root);
        var sdks = await Processes.Run("dotnet", ["--list-sdks"], root);
        var runtimes = await Processes.Run("dotnet", ["--list-runtimes"], root);
        var rows = new List<object>(); var edges = new List<object>();
        foreach (var project in projects)
        {
            var evaluation = await Projects.Evaluate(root, project); var properties = evaluation["Properties"]!; var items = evaluation["Items"];
            var targetFrameworks = Frameworks(properties).ToArray();
            var references = items?["ProjectReference"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>().Select(path => Rel(root, path)).Order(StringComparer.Ordinal).ToArray() ?? [];
            var packages = items?["PackageReference"]?.AsArray().Select(item => new { id = item?["Identity"]?.GetValue<string>(), version = ItemValue(item, "Version") }).OrderBy(item => item.id, StringComparer.Ordinal).ToArray() ?? [];
            var profile = TestProfileFor(root, properties, packages.Select(package => package.id).OfType<string>());
            rows.Add(new { path = Rel(root, project), language = Path.GetExtension(project) switch { ".fsproj" => "F#", ".vbproj" => "Visual Basic", _ => "C#" }, targetFrameworks, isTest = IsTrue(properties["IsTestProject"]), testPlatform = profile.Platform, testFramework = profile.Framework, testCommandMode = profile.CommandMode, projectReferences = references, packageReferences = packages });
            edges.AddRange(references.Select(reference => new { from = Rel(root, project), to = reference }));
        }
        JsonNode? globalJson = null; var globalJsonPath = Path.Combine(root, "global.json"); if (File.Exists(globalJsonPath)) globalJson = JsonNode.Parse(File.ReadAllText(globalJsonPath));
        return new
        {
            schemaVersion = 1,
            kind = "dotnet-inspection",
            environment = new { sdk = sdkVersion.ExitCode == 0 ? sdkVersion.Output.Trim() : null, installedSdks = Lines(sdks), installedRuntimes = Lines(runtimes), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription, globalJson },
            solutions = Projects.Solutions(root).Select(solution => Rel(root, solution)),
            projects = rows,
            graph = new { nodes = projects.Select(project => Rel(root, project)), edges }
        };
    }

    public static async Task<object> BuildPlan(string root, string? explicitProject, string? baseRef, string configuration, bool binlog)
    {
        ValidateConfiguration(configuration); root = Path.GetFullPath(root);
        var affected = explicitProject is null ? await Projects.Affected(root, await Git.Changed(root, baseRef)) : null;
        var targets = explicitProject is null ? affected!.Projects : ResolveTargets(root, explicitProject);
        var commands = new List<object>();
        foreach (var target in targets)
        {
            var relative = Rel(root, target); commands.Add(Command("restore", relative));
            var args = new List<string> { "build", relative, "--configuration", configuration, "--no-restore", "--nologo" };
            if (binlog) args.Add($"-bl:.agent-tool/binlogs/{SafeArtifactName(relative)}.binlog");
            commands.Add(Command(args));
        }
        return new { schemaVersion = 1, kind = "dotnet-build-plan", configuration, baseRef, targets = targets.Select(target => Rel(root, target)), selection = explicitProject is null ? affected!.Reason : "Explicit project or solution.", commands, artifacts = binlog ? new { binlogs = ".agent-tool/binlogs/*.binlog", retention = "Large binary artifacts stay outside model context; never send binlogs to JEV or a model.", analysisOrder = new[] { "structured-binlog-query", "bounded-text-log-fallback" }, structuredAnalyzer = "Microsoft.AITools.BinlogMcp (optional upstream integration)" } : null };
    }

    public static async Task<object> TestPlan(string root, string? explicitProject, string? baseRef, string configuration, TestSelection requested)
    {
        ValidateConfiguration(configuration); root = Path.GetFullPath(root); requested.Validate();
        string[] tests; string scope;
        if (explicitProject is null)
        {
            var affected = await Projects.Affected(root, await Git.Changed(root, baseRef));
            var selected = new List<string>(); foreach (var project in affected.Projects) if (await Projects.IsTest(root, project)) selected.Add(project);
            tests = selected.Order(StringComparer.Ordinal).ToArray(); scope = affected.Reason;
        }
        else
        {
            var target = Path.GetFullPath(explicitProject, root); if (!File.Exists(target)) throw new ArgumentException("Project or solution does not exist.");
            if (IsSolution(target))
            {
                var selected = new List<string>(); foreach (var project in await Projects.InSolution(root, target)) if (await Projects.IsTest(root, project)) selected.Add(project); tests = selected.ToArray();
            }
            else if (await Projects.IsTest(root, target)) tests = [target];
            else tests = await Projects.DependentTests(root, target);
            scope = "Explicit target and its transitive dependent test projects.";
        }
        var rows = new List<object>();
        foreach (var test in tests)
        {
            var evaluation = await Projects.Evaluate(root, test); var packages = evaluation["Items"]?["PackageReference"]?.AsArray().Select(item => item?["Identity"]?.GetValue<string>()).OfType<string>() ?? [];
            var profile = TestProfileFor(root, evaluation["Properties"]!, packages);
            var args = TestCommand(Rel(root, test), configuration, profile, requested);
            rows.Add(new { project = Rel(root, test), platform = profile.Platform, framework = profile.Framework, commandMode = profile.CommandMode, targetFrameworks = Frameworks(evaluation["Properties"]!).ToArray(), command = Command(args) });
        }
        return new { schemaVersion = 1, kind = "dotnet-test-plan", configuration, filter = requested.Filter, selection = scope, requestedSelection = requested, tests = rows, artifacts = new { results = ".agent-tool/test-results/**/*.trx", coverage = ".agent-tool/test-results/**/coverage.*.xml" } };
    }

    static string[] TestCommand(string project, string configuration, TestProfile profile, TestSelection selection)
    {
        if (profile.Platform is not ("vstest" or "microsoft-testing-platform"))
            throw new InvalidOperationException($"{project} has no recognized test platform. Load the test-platform edge-case reference; do not guess a command.");
        if (profile.Platform == "microsoft-testing-platform" && profile.CommandMode == "unconfigured")
            throw new InvalidOperationException($"{project} uses Microsoft.Testing.Platform but has neither SDK 10 native mode nor an executable VSTest bridge. Load the test-platform edge-case reference; do not guess a command.");
        var args = new List<string> { "test" };
        if (profile.CommandMode == "mtp-native") { args.Add("--project"); args.Add(project); }
        else args.Add(project);
        args.Add("--configuration"); args.Add(configuration); args.Add("--nologo");

        var runner = new List<string>();
        if (profile.Platform == "vstest") { runner.Add("--logger"); runner.Add("trx"); runner.Add("--results-directory"); runner.Add(".agent-tool/test-results"); }
        else { runner.Add("--report-trx"); runner.Add("--results-directory"); runner.Add(".agent-tool/test-results"); }
        runner.AddRange(FilterArguments(profile, selection));
        if (profile.CommandMode == "mtp-bridge") args.Add("--");
        args.AddRange(runner);
        return args.ToArray();
    }

    static string[] FilterArguments(TestProfile profile, TestSelection selection)
    {
        var value = selection.Value; if (value is null) return [];
        if (selection.Filter is not null)
        {
            if (profile.Platform == "microsoft-testing-platform" && profile.Framework is "xunit-v3" or "tunit" or "unknown")
                throw new InvalidOperationException($"Raw --filter is not portable to {profile.Framework} on Microsoft.Testing.Platform. Use --test, --class, or --category, or load the test-platform edge-case reference.");
            return ["--filter", value];
        }
        if (profile.Platform == "microsoft-testing-platform" && profile.Framework == "xunit-v3")
            return selection.Test is not null ? ["--filter-method", value] : selection.Class is not null ? ["--filter-class", value] : ["--filter-trait", $"Category={value}"];
        if (profile.Platform == "microsoft-testing-platform" && profile.Framework == "tunit")
            return ["--treenode-filter", selection.Test is not null ? $"/*/*/*/{value}" : selection.Class is not null ? $"/*/*/{value}/*" : $"/*/*/*/*[Category={value}]"];
        var expression = selection.Test is not null ? $"FullyQualifiedName={value}" : selection.Class is not null ? $"FullyQualifiedName~{value}" : $"TestCategory={value}";
        return ["--filter", expression];
    }

    public static async Task<object> DiagnosticsPlan(string? processId, string? requestedSignal = null, string? requestedDurationSeconds = null, string? workingDirectory = null)
    {
        int? pid = null;
        if (processId is not null)
        {
            if (!int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) throw new ArgumentException("--process-id must be a positive integer.");
            pid = parsed;
        }
        var signal = requestedSignal ?? "counters";
        if (signal is not ("counters" or "cpu" or "contention" or "allocations" or "managed-memory" or "crash" or "hang"))
            throw new ArgumentException("--signal must be counters, cpu, contention, allocations, managed-memory, crash, or hang.");
        var durationSeconds = 30;
        if (requestedDurationSeconds is not null && (!int.TryParse(requestedDurationSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out durationSeconds) || durationSeconds is < 5 or > 300))
            throw new ArgumentException("--duration-seconds must be an integer from 5 through 300.");
        var tools = new[] { "dotnet-counters", "dotnet-trace", "dotnet-dump", "dotnet-gcdump", "dotnet-monitor" }.Select(name => new { name, available = Processes.OnPath(name) }).ToArray();
        var dotnetVersion = await Processes.Run("dotnet", ["--version"], workingDirectory ?? Environment.CurrentDirectory);
        var runtimes = await Processes.Run("dotnet", ["--list-runtimes"], workingDirectory ?? Environment.CurrentDirectory);
        var duration = TimeSpan.FromSeconds(durationSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        object[] plans = pid is null ? [] : signal switch
        {
            "counters" => [ToolCommand("dotnet-counters", "monitor", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--duration", duration)],
            "managed-memory" => [ToolCommand("dotnet-gcdump", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--output", $".agent-tool/traces/process-{pid}-memory.gcdump")],
            "crash" or "hang" => [ToolCommand("dotnet-dump", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--output", $".agent-tool/traces/process-{pid}-{signal}.dmp")],
            _ => [ToolCommand("dotnet-trace", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--duration", duration, "--output", $".agent-tool/traces/process-{pid}-{signal}.nettrace")]
        };
        var selectedTool = signal switch { "counters" => "dotnet-counters", "managed-memory" => "dotnet-gcdump", "crash" or "hang" => "dotnet-dump", _ => "dotnet-trace" };
        return new
        {
            schemaVersion = 1,
            kind = "dotnet-diagnostics-plan",
            processId = pid,
            signal,
            durationSeconds,
            environment = new { os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription, sdk = dotnetVersion.ExitCode == 0 ? dotnetVersion.Output.Trim() : null, installedRuntimes = Lines(runtimes) },
            tools,
            collection = new { selectedTool, available = tools.Single(tool => tool.name == selectedTool).available, plans, artifactDirectory = ".agent-tool/traces", executesCollection = false },
            analysis = new { order = new[] { "existing-artifact", "bounded-structured-analysis", "collect-smallest-missing-signal" }, modelsReceive = "bounded sanitized summaries only" },
            safety = "Collection can expose secrets and personal data. Keep traces and dumps local, inspect size and sensitivity, and never send raw binary artifacts to JEV or a model."
        };
    }

    static string[] ResolveTargets(string root, string path)
    {
        var full = Path.GetFullPath(path, root); if (!File.Exists(full)) throw new ArgumentException("Project or solution does not exist."); return [full];
    }
    static bool IsSolution(string path) => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    static object Command(params string[] args) => Command((IEnumerable<string>)args);
    static object Command(IEnumerable<string> args) => new { executable = "dotnet", arguments = args.ToArray() };
    static object ToolCommand(string executable, params string[] args) => new { executable, arguments = args };
    static string Rel(string root, string path) => Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
    static string SafeArtifactName(string path) => Regex.Replace(path.Replace('\\', '-').Replace('/', '-'), "[^A-Za-z0-9_.-]", "-");
    static void ValidateConfiguration(string configuration) { if (!Regex.IsMatch(configuration, "^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Configuration must contain only letters, numbers, dot, underscore, or hyphen."); }
    static bool IsTrue(JsonNode? value) => string.Equals(value?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase);
    static IEnumerable<string> Frameworks(JsonNode properties) => (properties["TargetFrameworks"]?.GetValue<string>() is { Length: > 0 } frameworks ? frameworks : properties["TargetFramework"]?.GetValue<string>() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static string[] Lines(ProcessResult result) => result.ExitCode == 0 ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    static string? ItemValue(JsonNode? item, string name) => item?[name]?.GetValue<string>() ?? item?["Metadata"]?[name]?.GetValue<string>();
    internal static TestProfile DetectTestProfile(string root, JsonNode properties, IEnumerable<string> packages) => TestProfileFor(root, properties, packages);
    static TestProfile TestProfileFor(string root, JsonNode properties, IEnumerable<string> packages)
    {
        var names = packages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var framework = names.Any(name => name.Equals("TUnit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TUnit.", StringComparison.OrdinalIgnoreCase)) ? "tunit"
            : names.Any(name => name.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)) ? "xunit-v3"
            : names.Any(name => name.Equals("xunit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase)) ? "xunit-v2"
            : names.Any(name => name.Equals("NUnit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NUnit.", StringComparison.OrdinalIgnoreCase)) ? "nunit"
            : names.Any(name => name.Equals("MSTest", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSTest.", StringComparison.OrdinalIgnoreCase)) ? "mstest" : "unknown";
        var mtp = IsTrue(properties["IsTestingPlatformApplication"]) || IsTrue(properties["UseMicrosoftTestingPlatformRunner"]) || names.Contains("Microsoft.Testing.Platform") || names.Contains("MSTest.Sdk") || framework == "tunit";
        if (!mtp) return new(IsTrue(properties["IsTestProject"]) && names.Contains("Microsoft.NET.Test.Sdk") ? "vstest" : IsTrue(properties["IsTestProject"]) ? "unknown" : "not-test-project", framework, "vstest");
        var native = GlobalUsesNativeMtp(root);
        var bridge = IsTrue(properties["TestingPlatformDotnetTestSupport"]) && string.Equals(properties["OutputType"]?.GetValue<string>(), "Exe", StringComparison.OrdinalIgnoreCase);
        return new("microsoft-testing-platform", framework, native ? "mtp-native" : bridge ? "mtp-bridge" : "unconfigured");
    }

    static bool GlobalUsesNativeMtp(string root)
    {
        var path = Path.Combine(root, "global.json"); if (!File.Exists(path)) return false;
        var global = JsonNode.Parse(File.ReadAllText(path));
        var runner = global?["test"]?["runner"]?.GetValue<string>();
        var version = global?["sdk"]?["version"]?.GetValue<string>();
        return runner?.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) == true
            && Version.TryParse(version?.Split('-')[0], out var sdk) && sdk.Major >= 10;
    }
}

public record TestProfile(string Platform, string Framework, string CommandMode);
public record TestSelection(string? Test, string? Class, string? Category, string? Filter)
{
    public string? Value => Test ?? Class ?? Category ?? Filter;
    public void Validate()
    {
        var values = new[] { Test, Class, Category, Filter }.Where(value => value is not null).ToArray();
        if (values.Length > 1) throw new ArgumentException("Use only one of --test, --class, --category, or --filter.");
        if (values.Length == 0) return;
        if (string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 4096 || values[0]!.Any(char.IsControl)) throw new ArgumentException("Test selection must be 1-4096 printable characters.");
    }
}

public static class SafeFiles
{
    public static bool IsDiscoveryExcluded(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.Equals(".agent-results", StringComparison.Ordinal) || normalized.StartsWith(".agent-results/", StringComparison.Ordinal);
    }

    public static IEnumerable<string> Enumerate(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (new[] { ".git", ".agent-tool", ".agent-results", "bin", "obj", "node_modules", "artifacts", "TestResults", "runfile" }.Contains(Path.GetFileName(dir)) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in Enumerate(dir)) yield return file;
        }
    }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw new IOException("Refusing to write through a symlink in a managed state path.");
    }
    public static void Atomic(string path, string content)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class Output
{
    public static string[] Compact(string text, OutputSettings limits) => text.Split('\n').Select(x => Secrets.Redact(x.TrimEnd('\r'))).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(limits.MaxLines).Select(x => x.Length <= limits.MaxLineLength ? x : x[..limits.MaxLineLength] + "…").ToArray();
    public static object SummarizeFile(string path, OutputSettings limits)
    {
        var interesting = new List<string>(); var tail = new Queue<string>(); int lines = 0, errors = 0, warnings = 0;
        foreach (var line in File.ReadLines(path))
        {
            lines++;
            var zeroCount = Regex.IsMatch(line, @"^\s*0\s+(errors?|warnings?)\b", RegexOptions.IgnoreCase);
            var error = !zeroCount && Regex.IsMatch(line, @"\b(error|failed|failure|fatal)\b", RegexOptions.IgnoreCase);
            var warning = !zeroCount && Regex.IsMatch(line, @"\bwarning\b", RegexOptions.IgnoreCase);
            if (error) errors++; if (warning) warnings++;
            if ((error || warning) && interesting.Count < limits.MaxLines) interesting.Add(line);
            tail.Enqueue(line); if (tail.Count > limits.MaxLines) tail.Dequeue();
        }
        return new { lines, errorLines = errors, warningLines = warnings, evidence = Compact(string.Join('\n', interesting.Count > 0 ? interesting.AsEnumerable() : tail), limits), artifact = Path.GetFullPath(path), note = "Text counts are matching lines, not a build success verdict." };
    }
    public static object Sarif(string path, OutputSettings limits, string? baseline = null)
    {
        var current = SarifFindings(path, limits); var prior = baseline is null ? [] : SarifFindings(baseline, limits);
        var priorKeys = prior.Select(x => x.Key).ToHashSet(StringComparer.Ordinal); var currentKeys = current.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var added = current.Where(x => !priorKeys.Contains(x.Key)).ToArray(); var fixedFindings = prior.Where(x => !currentKeys.Contains(x.Key)).ToArray();
        return new
        {
            schemaVersion = 1,
            kind = "sarif-summary",
            count = current.Length,
            results = current.Take(limits.MaxItems).Select(x => x.Evidence),
            truncated = current.Length > limits.MaxItems,
            baseline = baseline is null ? null : new { artifact = Path.GetFullPath(baseline), count = prior.Length, added = added.Length, unchanged = current.Length - added.Length, fixedCount = fixedFindings.Length, newResults = added.Take(limits.MaxItems).Select(x => x.Evidence), newResultsTruncated = added.Length > limits.MaxItems, fixedResults = fixedFindings.Take(limits.MaxItems).Select(x => x.Evidence), fixedResultsTruncated = fixedFindings.Length > limits.MaxItems },
            artifact = Path.GetFullPath(path)
        };
    }
    static SarifFinding[] SarifFindings(string path, OutputSettings limits)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) throw new FormatException("SARIF has no runs array.");
        var rows = new List<SarifFinding>();
        foreach (var run in runs.EnumerateArray())
            if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array) foreach (var result in results.EnumerateArray())
                {
                    var rule = result.TryGetProperty("ruleId", out var ruleValue) ? ruleValue.GetString() : null;
                    var level = result.TryGetProperty("level", out var levelValue) ? levelValue.GetString() : "warning";
                    var message = result.TryGetProperty("message", out var messageValue) && messageValue.TryGetProperty("text", out var text) ? text.GetString() ?? "" : result.TryGetProperty("message", out messageValue) ? messageValue.ToString() : "";
                    string? uri = null; int? line = null;
                    if (result.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0)
                    {
                        var physical = locations[0].TryGetProperty("physicalLocation", out var physicalValue) ? physicalValue : default;
                        if (physical.ValueKind == JsonValueKind.Object && physical.TryGetProperty("artifactLocation", out var artifactLocation) && artifactLocation.TryGetProperty("uri", out var uriValue)) uri = uriValue.GetString();
                        if (physical.ValueKind == JsonValueKind.Object && physical.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var lineValue) && lineValue.TryGetInt32(out var parsedLine)) line = parsedLine;
                    }
                    var fingerprint = result.TryGetProperty("partialFingerprints", out var fingerprints) && fingerprints.ValueKind == JsonValueKind.Object
                        ? string.Join('|', fingerprints.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + x.Value.ToString())) : null;
                    var key = fingerprint is { Length: > 0 } ? rule + "|" + fingerprint : string.Join('|', rule, uri, line?.ToString(CultureInfo.InvariantCulture), message);
                    var suppression = result.TryGetProperty("suppressions", out var suppressions) && suppressions.ValueKind == JsonValueKind.Array ? suppressions.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
                    rows.Add(new(key, new { rule, level, message = Compact(message, limits), location = new { uri, startLine = line }, suppressions = suppression }));
                }
        return rows.ToArray();
    }
    sealed record SarifFinding(string Key, object Evidence);
}

public static class Artifacts
{
    public static string WritePromptManifestSizeMeasurement(string path, PromptManifestSizeMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.SchemaVersion != PromptManifestSizeMeasurement.CurrentSchemaVersion ||
            measurement.Kind != "prompt-manifest-size-measurement" ||
            measurement.MeasurementKind != PromptManifestSizeMeasurement.ProjectionKind ||
            measurement.RendererRevision != PromptManifestSizeMeasurement.ProjectionRevision)
            throw new ArgumentException("Unsupported prompt manifest measurement contract.", nameof(measurement));
        path = Path.GetFullPath(path);
        SafeFiles.Atomic(path, JsonSerializer.Serialize(measurement, InfrastructureJson.Options));
        return path;
    }

    public static object Inspect(string path, OutputSettings limits)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new ArgumentException("Artifact does not exist.");
        var info = new FileInfo(path); var sha256 = Hash(path); var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".zip") return new { schemaVersion = 1, kind = "artifact-inspection", path, format = "file", info.Length, sha256 };
        using var archive = System.IO.Compression.ZipFile.OpenRead(path); var names = new HashSet<string>(StringComparer.Ordinal); var entries = new List<object>(); var issues = new List<object>(); long unpacked = 0; int issueCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/'); unpacked += entry.Length; var reasons = new List<string>();
            if (name.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(name, @"^[A-Za-z]:/") || name.Split('/').Contains("..", StringComparer.Ordinal)) reasons.Add("path-traversal");
            if (!names.Add(name)) reasons.Add("duplicate-path");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) reasons.Add("symbolic-link");
            if (entry.Length > 1_000_000_000 || entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 1000) reasons.Add("expansion-risk");
            if (entries.Count < limits.MaxItems) entries.Add(new { path = name, entry.Length, entry.CompressedLength });
            issueCount += reasons.Count; foreach (var reason in reasons) if (issues.Count < limits.MaxItems) issues.Add(new { path = name, reason });
        }
        return new { schemaVersion = 1, kind = "artifact-inspection", path, format = "zip", info.Length, sha256, entryCount = archive.Entries.Count, unpackedBytes = unpacked, entries, entriesTruncated = archive.Entries.Count > entries.Count, issueCount, issues, issuesTruncated = issueCount > issues.Count, safeToExtract = issueCount == 0 };
    }
    public static Result Verify(string path, string expected)
    {
        if (!Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("--sha256 must be 64 hexadecimal characters.");
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new ArgumentException("Artifact does not exist.");
        var actual = Hash(path); var matches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
        return new(matches ? "ok" : "mismatch", new { schemaVersion = 1, kind = "artifact-verification", path, algorithm = "SHA-256", expected = expected.ToLowerInvariant(), actual, matches }, matches ? 0 : 1);
    }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}

public static class DotnetArtifacts
{
    public static object Dependencies(string path, string root, OutputSettings limits)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var rows = new List<object>(); int direct = 0, transitive = 0;
        if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array) throw new FormatException("NuGet package-list JSON has no projects array.");
        foreach (var project in projects.EnumerateArray())
        {
            var projectPath = project.TryGetProperty("path", out var projectValue) ? Relative(root, projectValue.GetString()) : null;
            if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateArray())
            {
                var tfm = framework.TryGetProperty("framework", out var frameworkValue) ? frameworkValue.GetString() : null;
                Add("topLevelPackages", true); Add("transitivePackages", false);
                void Add(string property, bool topLevel)
                {
                    if (!framework.TryGetProperty(property, out var packages)) return;
                    foreach (var package in packages.EnumerateArray())
                    {
                        if (topLevel) direct++; else transitive++;
                        if (rows.Count < limits.MaxItems) rows.Add(new { project = projectPath, framework = tfm, id = package.TryGetProperty("id", out var id) ? id.GetString() : null, direct = topLevel, requestedVersion = package.TryGetProperty("requestedVersion", out var requested) ? requested.GetString() : null, resolvedVersion = package.TryGetProperty("resolvedVersion", out var resolved) ? resolved.GetString() : null });
                    }
                }
            }
        }
        return new { schemaVersion = 1, kind = "dependency-inventory", direct, transitive, total = direct + transitive, packages = rows, truncated = direct + transitive > rows.Count, artifact = Path.GetFullPath(path) };
    }
    public static object TestResults(string path, OutputSettings limits)
    {
        var document = XDocument.Load(path, LoadOptions.None); var root = document.Root ?? throw new FormatException("Test results XML has no root element.");
        if (root.Name.LocalName == "TestRun") return Trx(path, root, limits);
        if (root.Name.LocalName is "testsuite" or "testsuites") return JUnit(path, root, limits);
        throw new FormatException("Unsupported test result XML. Expected TRX, JUnit testsuite, or JUnit testsuites.");
    }
    static object Trx(string path, XElement root, OutputSettings limits)
    {
        var results = root.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
        var outcomes = results.GroupBy(result => (string?)result.Attribute("outcome") ?? "Unknown", StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var failures = results.Where(result => !string.Equals((string?)result.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)).Take(limits.MaxItems).Select(result => new
        {
            test = (string?)result.Attribute("testName"),
            outcome = (string?)result.Attribute("outcome"),
            duration = (string?)result.Attribute("duration"),
            message = Output.Compact(result.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value ?? "", limits)
        }).ToArray();
        var duration = results.Select(result => TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var value) ? value : TimeSpan.Zero).Aggregate(TimeSpan.Zero, (total, value) => total + value);
        return new { schemaVersion = 1, kind = "test-results", format = "trx", total = results.Length, outcomes, durationMilliseconds = duration.TotalMilliseconds, failures, truncated = results.Count(result => !string.Equals((string?)result.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)) > failures.Length, artifact = Path.GetFullPath(path) };
    }
    static object JUnit(string path, XElement root, OutputSettings limits)
    {
        var cases = root.DescendantsAndSelf().Where(element => element.Name.LocalName == "testcase").ToArray();
        string Outcome(XElement test) => test.Elements().Any(element => element.Name.LocalName is "failure" or "error") ? "Failed" : test.Elements().Any(element => element.Name.LocalName == "skipped") ? "Skipped" : "Passed";
        var outcomes = cases.GroupBy(Outcome, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var failures = cases.Where(test => Outcome(test) == "Failed").Take(limits.MaxItems).Select(test => new { test = (string?)test.Attribute("name"), className = (string?)test.Attribute("classname"), outcome = "Failed", message = Output.Compact(test.Elements().First(element => element.Name.LocalName is "failure" or "error").Value, limits) }).ToArray();
        var seconds = cases.Sum(test => double.TryParse((string?)test.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0);
        return new { schemaVersion = 1, kind = "test-results", format = "junit", total = cases.Length, outcomes, durationMilliseconds = seconds * 1000, failures, truncated = outcomes.GetValueOrDefault("Failed") > failures.Length, artifact = Path.GetFullPath(path) };
    }
    public static object Coverage(string path, OutputSettings limits)
    {
        var document = XDocument.Load(path, LoadOptions.None); var root = document.Root ?? throw new FormatException("Coverage XML has no root element.");
        if (root.Name.LocalName == "coverage")
        {
            var linesValid = IntAttribute(root, "lines-valid"); var linesCovered = IntAttribute(root, "lines-covered"); var branchesValid = IntAttribute(root, "branches-valid"); var branchesCovered = IntAttribute(root, "branches-covered");
            var files = root.Descendants().Where(element => element.Name.LocalName == "class").Select(element => new { path = (string?)element.Attribute("filename"), lineRate = DoubleAttribute(element, "line-rate"), branchRate = DoubleAttribute(element, "branch-rate") }).OrderBy(item => item.path, StringComparer.Ordinal).Take(limits.MaxItems).ToArray();
            return CoverageResult(path, "cobertura", linesValid, linesCovered, branchesValid, branchesCovered, files);
        }
        if (root.Name.LocalName == "CoverageSession")
        {
            var summary = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Summary") ?? throw new FormatException("OpenCover summary is missing.");
            var sequence = IntAttribute(summary, "numSequencePoints"); var visitedSequence = IntAttribute(summary, "visitedSequencePoints"); var branches = IntAttribute(summary, "numBranchPoints"); var visitedBranches = IntAttribute(summary, "visitedBranchPoints");
            var files = root.Descendants().Where(element => element.Name.LocalName == "File").Select(element => new { id = (string?)element.Attribute("uid"), path = (string?)element.Attribute("fullPath") }).Take(limits.MaxItems).ToArray();
            return CoverageResult(path, "opencover", sequence, visitedSequence, branches, visitedBranches, files);
        }
        throw new FormatException("Unsupported coverage XML. Expected Cobertura or OpenCover.");
    }
    static object CoverageResult(string path, string format, int linesValid, int linesCovered, int branchesValid, int branchesCovered, object files) => new
    {
        schemaVersion = 1,
        kind = "coverage-summary",
        format,
        lines = new { valid = linesValid, covered = linesCovered, percent = Percent(linesCovered, linesValid) },
        branches = new { valid = branchesValid, covered = branchesCovered, percent = Percent(branchesCovered, branchesValid) },
        files,
        artifact = Path.GetFullPath(path)
    };
    static int IntAttribute(XElement element, string name) => int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    static double? DoubleAttribute(XElement element, string name) => double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    static double? Percent(int covered, int valid) => valid == 0 ? null : Math.Round(covered * 100d / valid, 2, MidpointRounding.AwayFromZero);
    static string? Relative(string root, string? path)
    {
        if (path is null) return null; var full = Path.GetFullPath(path, root); var relative = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/'); return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? full : relative;
    }
}
public static class Audit
{
    public static int Count(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(kv => kv.Key == "vulnerabilities" && kv.Value is JsonArray a ? a.Count : Count(kv.Value)),
        JsonArray arr => arr.Sum(Count),
        _ => 0
    };
}
