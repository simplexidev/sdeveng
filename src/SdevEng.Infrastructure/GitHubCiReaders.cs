using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

public sealed record GitHubCheck(int Id, string Name, string Status, string? Conclusion, Uri? DetailsUrl);
public sealed record GitHubWorkflow(long Id, string Name, string State, Uri HtmlUrl);
public sealed record GitHubWorkflowRun(long Id, string Name, string Status, string? Conclusion, Uri HtmlUrl);

public sealed record GitHubActionRun(long Id, string Workflow, string? Title, string Status, string? Conclusion, string Event, string? Branch, string? Sha, Uri Url, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);
public sealed record GitHubActionStep(string? Name, int? Number, string? Conclusion);
public sealed record GitHubActionJob(long Id, string? Name, string? Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Uri? Url, IReadOnlyList<GitHubActionStep> FailedSteps);
public sealed record GitHubActionRunDetail(GitHubActionRun Run, IReadOnlyList<GitHubActionJob> Jobs);
public sealed record GitHubActionFailedLog(long JobId, string? JobName, string Log);
public sealed record GitHubActionFailureEvidence(long RunId, string RunName, long JobId, string? JobName, IReadOnlyList<GitHubActionStep> FailedSteps, string Log, bool Truncated, string FailureClass = "unknown", string? ClassEvidence = null);

public static class GitHubFailureClassifier
{
    // First match wins: format, test, build, dependency, timeout, infrastructure.
    static readonly (string Class, Regex Pattern)[] Rules =
    [
        ("format", new Regex(@"\b(formatting|formatter|format check|dotnet format)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("test", new Regex(@"\b(test failed|tests? failed|assertion failed|failed tests?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("build", new Regex(@"\b(build failed|compilation failed|compiler error|error CS\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("dependency", new Regex(@"\b(package restore failed|unable to load the service index|NU\d{4}|dependency resolution failed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("timeout", new Regex(@"\b(timed out|timeout|time limit exceeded)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("infrastructure", new Regex(@"\b(runner lost|hosted runner|no space left on device|service unavailable|connection reset)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    ];

    public static (string Class, string? Evidence) Classify(string boundedExcerpt, string? conclusion = null)
    {
        var safe = Secrets.Redact(boundedExcerpt);
        if (conclusion == "cancelled") return ("unknown", null);
        if (conclusion == "timed_out") return ("timeout", "job-conclusion:timed_out");
        foreach (var line in safe.Split('\n'))
            foreach (var rule in Rules)
                if (rule.Pattern.IsMatch(line)) return (rule.Class, line.Length <= 300 ? line : line[..300]);
        return ("unknown", null);
    }
}

/// <summary>Reads bounded Actions run and failure evidence through the shared GitHub read transport.</summary>
public sealed class GitHubActionsReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");
    public async Task<IReadOnlyList<GitHubActionRun>> ReadRunsAsync(string owner, string repository, int limit, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var root = await ReadJsonAsync($"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/runs?per_page={limit.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        return Array(root, "workflow_runs").Select(ParseRun).ToArray();
    }
    public async Task<IReadOnlyList<GitHubActionRun>> ReadRunsForCommitAsync(string owner, string repository, string stepCommitSha, int limit, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        if (!Regex.IsMatch(stepCommitSha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A full commit SHA is required.", nameof(stepCommitSha));
        var root = await ReadJsonAsync($"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/runs?head_sha={Uri.EscapeDataString(stepCommitSha)}&per_page={limit.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        return Array(root, "workflow_runs").Select(ParseRun).Where(run => string.Equals(run.Sha, stepCommitSha, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();
    }
    public async Task<GitHubActionRunDetail> ReadRunAsync(string owner, string repository, long runId, int maxItems, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId); ValidateLimit(maxItems);
        var prefix = $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}";
        var run = ParseRun(await ReadJsonAsync(prefix, cancellationToken));
        var jobs = await ReadJsonAsync(prefix + "/jobs?per_page=" + maxItems.ToString(CultureInfo.InvariantCulture), cancellationToken);
        return new(run, Array(jobs, "jobs").Take(maxItems).Select(ParseJob).ToArray());
    }
    public async Task<IReadOnlyList<GitHubActionFailedLog>> ReadFailedLogsAsync(string owner, string repository, long runId, int maxItems, int maxOutputChars, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId); ValidateLimit(maxItems);
        if (maxOutputChars < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        var path = $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/jobs?per_page={maxItems.ToString(CultureInfo.InvariantCulture)}";
        var root = await ReadJsonAsync(path, cancellationToken);
        var failures = Array(root, "jobs").Select(ParseJob).Where(job => job.Conclusion is "failure" or "cancelled" or "timed_out" or "action_required").Take(maxItems).ToArray();
        var output = new List<GitHubActionFailedLog>(); var remaining = maxOutputChars;
        foreach (var job in failures)
        {
            if (remaining <= 0) break;
            using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/jobs/{job.Id.ToString(CultureInfo.InvariantCulture)}/logs"), cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var buffer = new char[Math.Min(remaining + 1, 8192)];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            var truncated = count > remaining;
            var marker = "\n[truncated]";
            if (truncated && marker.Length > remaining) marker = marker[..remaining];
            var length = truncated ? Math.Max(0, remaining - marker.Length) : count;
            output.Add(new(job.Id, job.Name, Secrets.Redact(new string(buffer, 0, length) + (truncated ? marker : ""))));
            remaining -= length + (truncated ? marker.Length : 0);
        }
        return output;
    }
    public async Task<GitHubActionFailureEvidence?> ReadFailureEvidenceAsync(string owner, string repository, long runId, string stepCommitSha, int maxOutputChars, CancellationToken cancellationToken = default)
    {
        ValidateRunId(runId);
        if (!Regex.IsMatch(stepCommitSha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)) throw new ArgumentException("A full commit SHA is required.", nameof(stepCommitSha));
        if (maxOutputChars < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        var prefix = $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}";
        var run = ParseRun(await ReadJsonAsync(prefix, cancellationToken));
        if (!string.Equals(run.Sha, stepCommitSha, StringComparison.OrdinalIgnoreCase)) return null;
        var jobsRoot = await ReadJsonAsync(prefix + "/jobs?per_page=200", cancellationToken);
        var job = Array(jobsRoot, "jobs").Select(ParseJob)
            .Where(item => item.Conclusion is "failure" or "timed_out" or "action_required")
            .OrderBy(item => item.StartedAt ?? DateTimeOffset.MaxValue).ThenBy(item => item.Id).FirstOrDefault();
        if (job is null) return null;
        using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/actions/jobs/{job.Id.ToString(CultureInfo.InvariantCulture)}/logs"), cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[maxOutputChars + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        var truncated = count > maxOutputChars;
        const string marker = "\n[truncated]";
        var textLength = truncated ? Math.Max(0, maxOutputChars - marker.Length) : count;
        var log = new string(buffer, 0, textLength) + (truncated ? marker[..Math.Min(marker.Length, maxOutputChars)] : "");
        log = Secrets.Redact(log);
        var classification = GitHubFailureClassifier.Classify(log, job.Conclusion);
        return new(runId, run.Workflow, job.Id, job.Name, job.FailedSteps, log, truncated, classification.Class, classification.Evidence);
    }
    async Task<JsonElement> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, path), cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }
    static GitHubActionRun ParseRun(JsonElement value) => new(Long(value, "id"), Nested(value, "workflow", "name") ?? Text(value, "name") ?? "", Text(value, "display_title"), RequiredText(value, "status"), Text(value, "conclusion"), RequiredText(value, "event"), Text(value, "head_branch"), Text(value, "head_sha"), RequiredUrl(value, "html_url"), Date(value, "created_at"), Date(value, "updated_at"));
    static GitHubActionJob ParseJob(JsonElement value)
    {
        var steps = value.TryGetProperty("steps", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(step => Text(step, "conclusion") is "failure" or "cancelled" or "timed_out").Select(step => new GitHubActionStep(Text(step, "name"), Int(step, "number"), Text(step, "conclusion"))).ToArray() : System.Array.Empty<GitHubActionStep>();
        return new(Long(value, "id"), Text(value, "name"), Text(value, "status"), Text(value, "conclusion"), Date(value, "started_at"), Date(value, "completed_at"), OptionalUrl(value, "html_url"), steps);
    }
    static void ValidateRunId(long id) { if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id)); }
    static void ValidateLimit(int value) { if (value is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(value)); }
    static string Part(string value, string name) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} must be a GitHub owner or repository name.", name);
    static JsonElement[] Array(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : throw new JsonException($"GitHub response is missing '{name}'.");
    static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    static string RequiredText(JsonElement value, string name) => Text(value, name) is { Length: > 0 } text ? text : throw new JsonException($"GitHub Actions response is missing '{name}'.");
    static string? Nested(JsonElement value, string parent, string child) => value.TryGetProperty(parent, out var item) && item.ValueKind == JsonValueKind.Object ? Text(item, child) : null;
    static long Long(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt64(out var result) && result > 0 ? result : throw new JsonException($"GitHub Actions response has an invalid '{name}'.");
    static int? Int(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var result) ? result : null;
    static DateTimeOffset? Date(JsonElement value, string name) => DateTimeOffset.TryParse(Text(value, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
    static Uri RequiredUrl(JsonElement value, string name) => OptionalUrl(value, name) ?? throw new JsonException($"GitHub Actions response has an invalid '{name}'.");
    static Uri? OptionalUrl(JsonElement value, string name) => Uri.TryCreate(Text(value, name), UriKind.Absolute, out var result) && result.Scheme == Uri.UriSchemeHttps ? result : null;
}

/// <summary>Reads typed check and workflow data through the shared GitHub read transport.</summary>
public sealed class GitHubChecksWorkflowReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");

    public async Task<IReadOnlyList<GitHubCheck>> ReadChecksAsync(string owner, string repository, string reference, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync($"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/commits/{Segment(reference, nameof(reference))}/check-runs", cancellationToken);
        return Array(root, "check_runs").Select(item => new GitHubCheck(Integer(item, "id"), RequiredString(item, "name"), RequiredString(item, "status"), OptionalString(item, "conclusion"), OptionalUrl(item, "html_url"))).ToArray();
    }

    public async Task<IReadOnlyList<GitHubWorkflow>> ReadWorkflowsAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync($"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/actions/workflows", cancellationToken);
        return Array(root, "workflows").Select(item => new GitHubWorkflow(Long(item, "id"), RequiredString(item, "name"), RequiredString(item, "state"), RequiredUrl(item, "html_url"))).ToArray();
    }

    public async Task<IReadOnlyList<GitHubWorkflowRun>> ReadWorkflowRunsAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync($"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/actions/runs", cancellationToken);
        return Array(root, "workflow_runs").Select(item => new GitHubWorkflowRun(Long(item, "id"), RequiredString(item, "name"), RequiredString(item, "status"), OptionalString(item, "conclusion"), RequiredUrl(item, "html_url"))).ToArray();
    }

    async Task<JsonElement> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, path), cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    static string Segment(string value, string name) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
        ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} must be a GitHub owner, repository, or reference.", name);
    static JsonElement[] Array(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().ToArray() : throw new JsonException($"GitHub response is missing '{name}'.");
    static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new JsonException($"GitHub response is missing '{name}'.");
    static string? OptionalString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static int Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number > 0 ? number : throw new JsonException($"GitHub response has an invalid '{name}'.");
    static long Long(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number > 0 ? number : throw new JsonException($"GitHub response has an invalid '{name}'.");
    static Uri RequiredUrl(JsonElement root, string name) => OptionalUrl(root, name) ?? throw new JsonException($"GitHub response has an invalid '{name}'.");
    static Uri? OptionalUrl(JsonElement root, string name) => Uri.TryCreate(OptionalString(root, name), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
}

public sealed record CiObservationCheck(int Id, string Name, Uri? DetailsUrl, string ProviderStatus, string? ProviderConclusion, string State);
public sealed record CiObservation(string CommitSha, IReadOnlyList<CiObservationCheck> Checks, IReadOnlyList<GitHubActionRun> WorkflowRuns);

/// <summary>Builds bounded CI facts for the commit recorded on a product run.</summary>
public sealed class CiObservationService(GitHubChecksWorkflowReader checksReader, GitHubActionsReader actionsReader)
{
    public async Task<GitHubActionFailureEvidence?> ReadLatestFailureEvidenceAsync(string runDirectory, Guid productRunId, string owner, string repository, int maxOutputChars = 16000, CancellationToken cancellationToken = default)
    {
        var events = LocalRunEventStore.Read(runDirectory, productRunId);
        var commitEvent = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() is "commit" or "step-commit");
        var sha = commitEvent.ValueKind == JsonValueKind.Undefined ? null : commitEvent.GetProperty("identifier").GetString();
        if (sha is null || !Regex.IsMatch(sha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)) throw new ArgumentException("A full persisted commit SHA is required.");
        var snapshot = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "ci-check-snapshot");
        if (snapshot.ValueKind == JsonValueKind.Undefined || !string.Equals(snapshot.GetProperty("commitSha").GetString(), sha, StringComparison.OrdinalIgnoreCase)) return null;
        var failedRun = snapshot.GetProperty("workflowRuns").EnumerateArray()
            .Where(item => item.GetProperty("state").GetString() is "failure" or "timed_out" or "action_required")
            .Select(item => long.Parse(item.GetProperty("id").GetString()!, CultureInfo.InvariantCulture)).OrderBy(id => id).FirstOrDefault();
        return failedRun == 0 ? null : await actionsReader.ReadFailureEvidenceAsync(owner, repository, failedRun, sha, maxOutputChars, cancellationToken);
    }

    public async Task<CiObservation> ObserveAsync(string runDirectory, Guid productRunId, string owner, string repository,
        string? explicitCommitSha = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var events = LocalRunEventStore.Read(runDirectory, productRunId);
        var commit = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
            item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() is "commit" or "step-commit");
        var sha = commit.ValueKind == JsonValueKind.Undefined ? explicitCommitSha : commit.GetProperty("identifier").GetString();
        if (sha is null || !Regex.IsMatch(sha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A full commit SHA is required when run metadata has no valid commit.", nameof(explicitCommitSha));
        var checks = await checksReader.ReadChecksAsync(owner, repository, sha, cancellationToken);
        var runs = await actionsReader.ReadRunsForCommitAsync(owner, repository, sha, limit, cancellationToken);
        var observation = new CiObservation(sha, checks.Take(limit).Select(check => new CiObservationCheck(check.Id, check.Name, check.DetailsUrl,
            check.Status, check.Conclusion, Normalize(check.Status, check.Conclusion))).ToArray(), runs);
        LocalRunEventStore.AppendCiCheckSnapshot(runDirectory, productRunId, observation.CommitSha, DateTimeOffset.UtcNow,
            observation.Checks.Take(100).Select(check => check.State).ToArray(), observation.WorkflowRuns.Take(100).Select(run => (run.Id.ToString(CultureInfo.InvariantCulture),
                run.Conclusion ?? run.Status)).ToArray());
        return observation;
    }

    static string Normalize(string status, string? conclusion)
    {
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) return "pending";
        return conclusion?.ToLowerInvariant() switch
        {
            "success" or "neutral" => "success",
            "failure" or "timed_out" or "action_required" => "failure",
            "cancelled" => "cancelled",
            "skipped" => "skipped",
            _ => "unknown"
        };
    }
}
