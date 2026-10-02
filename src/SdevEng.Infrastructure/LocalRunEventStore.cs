using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SdevEng;

public static class LocalRunEventStore
{
    public static object List(string directory)
    {
        var runs = Directory.Exists(directory)
            ? Directory.GetDirectories(directory).Where(path => Guid.TryParseExact(Path.GetFileName(path), "D", out var id) && id != Guid.Empty)
                .Select(path => new { runId = Path.GetFileName(path), status = Status(directory, Guid.Parse(Path.GetFileName(path))) })
                .OrderBy(run => run.runId, StringComparer.Ordinal).ToArray()
            : [];
        return new { kind = "run-list", runs };
    }

    public static JsonElement AppendTransition(string directory, Guid runId, string? fromState, string toState) =>
        Append(directory, runId, "state-transition", fromState, toState, null, null, null);

    public static JsonElement AppendOperationCompleted(string directory, Guid runId, string operation) =>
        Append(directory, runId, "operation-completed", null, null, null, null, operation);

    public static JsonElement AppendStartWorkProgress(string directory, Guid runId, string operation, string status, string detail = "")
    {
        if (!StartWorkOperation(operation) || status is not ("completed" or "retryable-failure" or "terminal-failure"))
            throw new ArgumentException("Invalid start-work progress operation or status.");
        detail = Secrets.Redact(detail);
        if (detail.Length > 512) detail = detail[..512];
        return Append(directory, runId, "start-work-progress", null, null, null, status, operation, detail);
    }

    private static bool StartWorkOperation(string? operation) => operation is "branch-created" or "branch-pushed" or
        "bootstrap-created" or "bootstrap-cleanup" or "pr-created" or "pr-linked" or "metadata-persisted";

    public static JsonElement AppendExternalIdentifier(string directory, Guid runId, string externalSystem, string identifierType, string identifier)
    {
        if (externalSystem == "git" && identifierType == "step-commit")
            throw new ArgumentException("New run events must use the commit identifier type.", nameof(identifierType));
        return Append(directory, runId, "external-identifier-recorded", null, null, externalSystem, identifierType, identifier);
    }

    public static JsonElement AppendRepositoryIdentifier(string directory, Guid runId, string repository) =>
        AppendExternalIdentifier(directory, runId, "git", "repository", repository);

    public static JsonElement AppendBranchIdentifier(string directory, Guid runId, string branch) =>
        AppendExternalIdentifier(directory, runId, "git", "branch", branch);

    public static JsonElement AppendIssueIdentifier(string directory, Guid runId, string issue) =>
        AppendExternalIdentifier(directory, runId, "github", "issue", issue);

    public static JsonElement AppendPullRequestIdentifier(string directory, Guid runId, string pullRequest) =>
        AppendExternalIdentifier(directory, runId, "github", "pull-request", pullRequest);

    public static void AppendConfirmedPullRequestIdentity(string directory, Guid runId, string repository, int number)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) || number <= 0)
            throw new ArgumentException("Pull request identity requires a repository and positive number.");
        if (!Read(directory, runId).Any(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() == "repository" && item.GetProperty("identifier").GetString() == repository))
            throw new InvalidDataException("Pull request identity repository does not match the recorded origin.");
        var url = $"https://github.com/{repository}/pull/{number.ToString(CultureInfo.InvariantCulture)}";
        var existing = Read(directory, runId).Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
            item.GetProperty("externalSystem").GetString() == "github" && item.GetProperty("identifierType").GetString() is "pull-request-number" or "pull-request-url").ToArray();
        var expected = new Dictionary<string, string> { ["pull-request-number"] = number.ToString(CultureInfo.InvariantCulture), ["pull-request-url"] = url };
        foreach (var item in existing)
            if (!expected.TryGetValue(item.GetProperty("identifierType").GetString()!, out var value) || item.GetProperty("identifier").GetString() != value)
                throw new InvalidDataException("Conflicting pull request identity metadata.");
        if (!existing.Any(item => item.GetProperty("identifierType").GetString() == "pull-request-number"))
            AppendExternalIdentifier(directory, runId, "github", "pull-request-number", expected["pull-request-number"]);
        if (!existing.Any(item => item.GetProperty("identifierType").GetString() == "pull-request-url"))
            AppendExternalIdentifier(directory, runId, "github", "pull-request-url", url);
    }

    public static JsonElement AppendCommitIdentifier(string directory, Guid runId, string commit) =>
        AppendExternalIdentifier(directory, runId, "git", "commit", commit);

    [Obsolete("Use AppendCommitIdentifier; legacy step-commit events remain readable.")]
    public static JsonElement AppendStepCommitIdentifier(string directory, Guid runId, string commit) =>
        AppendCommitIdentifier(directory, runId, commit);

    public static JsonElement AppendCiRunIdentifier(string directory, Guid runId, string ciRun) =>
        AppendExternalIdentifier(directory, runId, "github-actions", "ci-run", ciRun);

    public static JsonElement AppendCiCheckSnapshot(string directory, Guid runId, string commitSha, DateTimeOffset observedAt,
        IReadOnlyList<string> checks, IReadOnlyList<(string Id, string State)> workflowRuns)
    {
        if (!Regex.IsMatch(commitSha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant) ||
            checks.Count > 100 || workflowRuns.Count > 100 || checks.Any(state => state is not ("pending" or "success" or "failure" or "cancelled" or "skipped" or "unknown")) ||
            workflowRuns.Any(run => !Regex.IsMatch(run.Id, @"^[1-9][0-9]*$", RegexOptions.CultureInvariant) || run.State.Length is < 1 or > 32))
            throw new ArgumentException("CI snapshot contains invalid or unbounded facts.");
        var payload = new
        {
            schemaVersion = 1,
            runId = runId.ToString("D"),
            sequence = 0,
            occurredAt = DateTimeOffset.UtcNow,
            eventType = "ci-check-snapshot",
            snapshotVersion = 1,
            commitSha = commitSha.ToLowerInvariant(),
            observedAt,
            checks = checks.Select(state => new { state }).ToArray(),
            workflowRuns = workflowRuns.Select(run => new { id = run.Id, state = run.State }).ToArray()
        };
        return AppendSnapshot(directory, runId, payload);
    }

    public static JsonElement AppendCiFailureEvidence(string directory, Guid runId, string commitSha, string providerRunId,
        string providerJobId, string failureClass, bool truncated, string excerpt)
    {
        if (runId == Guid.Empty || !Regex.IsMatch(commitSha, @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(providerRunId, @"^[1-9][0-9]*$", RegexOptions.CultureInvariant) || providerRunId.Length > 32 ||
            !Regex.IsMatch(providerJobId, @"^[1-9][0-9]*$", RegexOptions.CultureInvariant) || providerJobId.Length > 32 ||
            string.IsNullOrWhiteSpace(failureClass) || failureClass.Length > 64 || string.IsNullOrWhiteSpace(excerpt))
            throw new ArgumentException("CI failure evidence has invalid or empty identity fields.");
        excerpt = Secrets.Redact(excerpt);
        if (excerpt.Length > 4096) { excerpt = excerpt[..4096]; truncated = true; }
        if (string.IsNullOrWhiteSpace(excerpt)) throw new ArgumentException("CI failure evidence excerpt must be nonempty.");
        var payload = new
        {
            schemaVersion = 1,
            runId = runId.ToString("D"),
            sequence = 0,
            occurredAt = DateTimeOffset.UtcNow,
            eventType = "ci-failure-evidence",
            evidenceVersion = 1,
            commitSha = commitSha.ToLowerInvariant(),
            providerRunId,
            providerJobId,
            failureClass,
            truncated,
            excerpt
        };
        return AppendSnapshot(directory, runId, payload);
    }

    public static JsonElement AppendCiRerunEvent(string directory, Guid runId, string repository, string commitSha, string providerRunId, string providerJobId, string mode, string reason)
    {
        if (mode is not ("job" or "failed-jobs") || string.IsNullOrWhiteSpace(reason) || reason.Length > 256)
            throw new ArgumentException("Rerun mode and bounded nonempty reason are required.");
        reason = Secrets.Redact(reason);
        var events = Read(directory, runId);
        var failure = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "ci-failure-evidence" &&
            item.GetProperty("commitSha").GetString() == commitSha.ToLowerInvariant() && item.GetProperty("providerRunId").GetString() == providerRunId &&
            (providerJobId == "0" || item.GetProperty("providerJobId").GetString() == providerJobId));
        if (failure.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Matching CI failure evidence is required for rerun accounting.");
        var failureClass = failure.GetProperty("failureClass").GetString()!;
        var signature = ComputeCiFailureSignature(repository, commitSha, providerRunId, providerJobId, failureClass);
        var prior = events.Where(item => item.GetProperty("eventType").GetString() == "ci-rerun" && item.GetProperty("failureSignature").GetString() == signature).ToArray();
        if (prior.Length >= 1) throw new InvalidOperationException("This failure identity already has a rerun request.");
        var payload = new
        {
            schemaVersion = 1,
            runId = runId.ToString("D"),
            sequence = 0,
            occurredAt = DateTimeOffset.UtcNow,
            eventType = "ci-rerun",
            rerunVersion = 1,
            repository,
            commitSha = commitSha.ToLowerInvariant(),
            providerRunId,
            providerJobId,
            rerunMode = mode,
            rerunReason = reason,
            failureSignature = signature,
            ordinal = prior.Length + 1
        };
        return AppendSnapshot(directory, runId, payload);
    }

    public static string ComputeCiFailureSignature(string repository, string commitSha, string providerRunId, string providerJobId, string failureClass)
    {
        var input = string.Join("\n", repository.ToLowerInvariant(), commitSha.ToLowerInvariant(), providerRunId, providerJobId == "" ? "0" : providerJobId, failureClass);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static JsonElement AppendSnapshot(string directory, Guid runId, object snapshot)
    {
        var path = Path.Combine(directory, runId.ToString("D"));
        Directory.CreateDirectory(path);
        using var gate = new FileStream(Path.Combine(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var node = JsonSerializer.SerializeToNode(snapshot, InfrastructureJson.Options)!;
        node["sequence"] = Read(directory, runId).Count + 1;
        var file = Path.Combine(path, node["sequence"]!.GetValue<int>().ToString("D20", CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(file, node.ToJsonString(InfrastructureJson.Options) + "\n", new UTF8Encoding(false));
        using var document = JsonDocument.Parse(node.ToJsonString(InfrastructureJson.Options));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<JsonElement> Read(string directory, Guid runId)
    {
        var path = Path.Combine(directory, runId.ToString("D"));
        if (!Directory.Exists(path)) return [];
        var files = Directory.GetFiles(path, "*.json").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var events = new List<JsonElement>(files.Length);
        string? currentState = null;
        var hasTransition = false;
        for (var index = 0; index < files.Length; index++)
        {
            var expected = (index + 1).ToString("D20", CultureInfo.InvariantCulture) + ".json";
            if (Path.GetFileName(files[index]) != expected) throw new InvalidDataException("Run event sequence has a gap.");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(files[index]));
                var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object ||
                    item.GetProperty("schemaVersion").GetInt32() != 1 ||
                    item.GetProperty("runId").GetString() != runId.ToString("D") ||
                    item.GetProperty("sequence").GetInt32() != index + 1 ||
                    !DateTimeOffset.TryParse(item.GetProperty("occurredAt").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    throw new InvalidDataException("Run event identity, sequence, or timestamp is invalid.");
                var type = item.GetProperty("eventType").GetString();
                var propertyNames = item.EnumerateObject().Select(property => property.Name).ToArray();
                var properties = propertyNames.ToHashSet(StringComparer.Ordinal);
                if (properties.Count != propertyNames.Length) throw new InvalidDataException("Run event has duplicate properties.");
                if (type == "state-transition")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "fromState", "toState"]) ||
                        item.GetProperty("fromState").ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                        item.GetProperty("toState").ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.GetProperty("toState").GetString()) ||
                        item.GetProperty("fromState").GetString() != currentState ||
                        !hasTransition && item.GetProperty("fromState").ValueKind != JsonValueKind.Null)
                        throw new InvalidDataException("Run state transition is invalid.");
                    currentState = item.GetProperty("toState").GetString();
                    hasTransition = true;
                }
                else if (type == "external-identifier-recorded")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "externalSystem", "identifierType", "identifier"]) ||
                        new[] { "externalSystem", "identifierType", "identifier" }.Any(name =>
                            item.GetProperty(name).ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetProperty(name).GetString())))
                        throw new InvalidDataException("Run external identifier is invalid.");
                    var system = item.GetProperty("externalSystem").GetString();
                    var identifierType = item.GetProperty("identifierType").GetString();
                    var identifier = item.GetProperty("identifier").GetString()!;
                    if (system == "github" && identifierType == "pull-request-number" &&
                        (!int.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var prNumber) || prNumber <= 0 || identifier != prNumber.ToString(CultureInfo.InvariantCulture)))
                        throw new InvalidDataException("Pull request number identifier is invalid.");
                    if (system == "github" && identifierType == "pull-request-url" &&
                        (!Uri.TryCreate(identifier, UriKind.Absolute, out var prUrl) || prUrl is null || prUrl.Scheme != Uri.UriSchemeHttps || prUrl.Query.Length != 0 || prUrl.Fragment.Length != 0 ||
                         !Regex.IsMatch(prUrl.AbsolutePath, @"^/[^/]+/[^/]+/pull/[1-9][0-9]*$", RegexOptions.CultureInvariant) || prUrl.GetLeftPart(UriPartial.Path) != identifier))
                        throw new InvalidDataException("Pull request URL identifier is invalid.");
                    if (system == "github" && identifierType == "pull-request-url")
                    {
                        var repo = events.LastOrDefault(existing => existing.GetProperty("eventType").GetString() == "external-identifier-recorded" && existing.GetProperty("externalSystem").GetString() == "git" && existing.GetProperty("identifierType").GetString() == "repository");
                        if (repo.ValueKind == JsonValueKind.Undefined || !identifier.StartsWith($"https://github.com/{repo.GetProperty("identifier").GetString()}/pull/", StringComparison.Ordinal))
                            throw new InvalidDataException("Pull request URL does not match the recorded origin repository.");
                        var number = events.LastOrDefault(existing => existing.GetProperty("eventType").GetString() == "external-identifier-recorded" && existing.GetProperty("externalSystem").GetString() == "github" && existing.GetProperty("identifierType").GetString() == "pull-request-number");
                        if (number.ValueKind != JsonValueKind.Undefined && !identifier.EndsWith("/" + number.GetProperty("identifier").GetString(), StringComparison.Ordinal))
                            throw new InvalidDataException("Pull request URL does not match its recorded number.");
                    }
                    if (system == "github" && identifierType is "pull-request-number" or "pull-request-url")
                    {
                        var prior = events.Where(existing => existing.GetProperty("eventType").GetString() == "external-identifier-recorded" && existing.GetProperty("externalSystem").GetString() == system && existing.GetProperty("identifierType").GetString() == identifierType);
                        if (prior.Any(existing => existing.GetProperty("identifier").GetString() != identifier))
                            throw new InvalidDataException("Conflicting pull request identity metadata.");
                    }
                }
                else if (type == "operation-completed")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "operation"]) ||
                        item.GetProperty("operation").GetString() is not ("branch-created" or "branch-pushed"))
                        throw new InvalidDataException("Run operation completion is invalid.");
                }
                else if (type == "start-work-progress")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "progressVersion", "operation", "status", "detail"]) ||
                        item.GetProperty("progressVersion").GetInt32() != 1 ||
                        !StartWorkOperation(item.GetProperty("operation").GetString()) ||
                        item.GetProperty("status").GetString() is not ("completed" or "retryable-failure" or "terminal-failure") ||
                        item.GetProperty("detail").ValueKind != JsonValueKind.String ||
                        item.GetProperty("detail").GetString()!.Length > 512 || Secrets.LooksSensitive(item.GetProperty("detail").GetString()!))
                        throw new InvalidDataException("Run start-work progress is invalid.");
                }
                else if (type == "ci-check-snapshot")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "snapshotVersion", "commitSha", "observedAt", "checks", "workflowRuns"]) ||
                        item.GetProperty("snapshotVersion").GetInt32() != 1 || !Regex.IsMatch(item.GetProperty("commitSha").GetString() ?? "", @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant) ||
                        !DateTimeOffset.TryParse(item.GetProperty("observedAt").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ||
                        item.GetProperty("checks").ValueKind != JsonValueKind.Array || item.GetProperty("checks").GetArrayLength() > 100 ||
                        item.GetProperty("workflowRuns").ValueKind != JsonValueKind.Array || item.GetProperty("workflowRuns").GetArrayLength() > 100 ||
                        item.GetProperty("checks").EnumerateArray().Any(check => check.EnumerateObject().Count() != 1 || check.GetProperty("state").GetString() is not ("pending" or "success" or "failure" or "cancelled" or "skipped" or "unknown")) ||
                        item.GetProperty("workflowRuns").EnumerateArray().Any(run => run.EnumerateObject().Count() != 2 || !Regex.IsMatch(run.GetProperty("id").GetString() ?? "", @"^[1-9][0-9]*$", RegexOptions.CultureInvariant) || string.IsNullOrWhiteSpace(run.GetProperty("state").GetString()) || run.GetProperty("state").GetString()!.Length > 32))
                        throw new InvalidDataException("Run CI check snapshot is invalid.");
                }
                else if (type == "ci-failure-evidence")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "evidenceVersion", "commitSha", "providerRunId", "providerJobId", "failureClass", "truncated", "excerpt"]) ||
                        item.GetProperty("evidenceVersion").GetInt32() != 1 || !Regex.IsMatch(item.GetProperty("commitSha").GetString() ?? "", @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant) ||
                        !Regex.IsMatch(item.GetProperty("providerRunId").GetString() ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant) ||
                        !Regex.IsMatch(item.GetProperty("providerJobId").GetString() ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant) ||
                        string.IsNullOrWhiteSpace(item.GetProperty("failureClass").GetString()) || item.GetProperty("failureClass").GetString()!.Length > 64 ||
                        item.GetProperty("truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        item.GetProperty("excerpt").ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetProperty("excerpt").GetString()) ||
                        item.GetProperty("excerpt").GetString()!.Length > 4096 || Secrets.LooksSensitive(item.GetProperty("excerpt").GetString()!))
                        throw new InvalidDataException("Run CI failure evidence is invalid.");
                }
                else if (type == "ci-rerun")
                {
                    if (!properties.SetEquals(["schemaVersion", "runId", "sequence", "occurredAt", "eventType", "rerunVersion", "repository", "commitSha", "providerRunId", "providerJobId", "rerunMode", "rerunReason", "failureSignature", "ordinal"]) ||
                        item.GetProperty("rerunVersion").GetInt32() != 1 ||
                        !Regex.IsMatch(item.GetProperty("failureSignature").GetString() ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) || item.GetProperty("ordinal").GetInt32() < 1 ||
                        item.GetProperty("rerunMode").GetString() is not ("job" or "failed-jobs") ||
                        string.IsNullOrWhiteSpace(item.GetProperty("rerunReason").GetString()) || item.GetProperty("rerunReason").GetString()!.Length > 256 || Secrets.LooksSensitive(item.GetProperty("rerunReason").GetString()!) ||
                        !Regex.IsMatch(item.GetProperty("repository").GetString() ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
                        !Regex.IsMatch(item.GetProperty("commitSha").GetString() ?? "", @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant) ||
                        !Regex.IsMatch(item.GetProperty("providerRunId").GetString() ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant) ||
                        !Regex.IsMatch(item.GetProperty("providerJobId").GetString() ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant))
                        throw new InvalidDataException("Run CI rerun event is invalid.");
                }
                else throw new InvalidDataException("Run event type is invalid.");
                events.Add(item.Clone());
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("Run event is malformed.", exception);
            }
        }
        return events;
    }

    public static object Status(string directory, Guid runId)
    {
        var events = Read(directory, runId);
        var transitions = events.Where(item => item.GetProperty("eventType").GetString() == "state-transition").ToArray();
        var identifiers = events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded")
            .Select(item => new { system = item.GetProperty("externalSystem").GetString(), type = item.GetProperty("identifierType").GetString(), value = item.GetProperty("identifier").GetString() }).ToArray();
        return new { kind = "run-status", runId = runId.ToString("D"), state = transitions.LastOrDefault().ValueKind == JsonValueKind.Undefined ? null : transitions[^1].GetProperty("toState").GetString(), eventCount = events.Count, identifiers };
    }

    public static object Explain(string directory, Guid runId)
    {
        var events = Read(directory, runId);
        var snapshot = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "ci-check-snapshot");
        var failureEvidence = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "ci-failure-evidence");
        var timeline = events.Select(item => item.GetProperty("eventType").GetString() == "start-work-progress"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "start-work-progress", from = item.GetProperty("operation").GetString(), to = item.GetProperty("status").GetString() }
            : item.GetProperty("eventType").GetString() == "operation-completed"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "operation-completed", from = (string?)null, to = item.GetProperty("operation").GetString() }
            : item.GetProperty("eventType").GetString() == "state-transition"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "state-transition", from = item.GetProperty("fromState").GetString(), to = item.GetProperty("toState").GetString() }
            : item.GetProperty("eventType").GetString() == "ci-check-snapshot"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "ci-check-snapshot", from = (string?)null, to = item.GetProperty("commitSha").GetString() }
            : item.GetProperty("eventType").GetString() == "ci-failure-evidence"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "ci-failure-evidence", from = (string?)null, to = item.GetProperty("failureClass").GetString() }
            : item.GetProperty("eventType").GetString() == "ci-rerun"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "ci-rerun", from = (string?)null, to = item.GetProperty("failureSignature").GetString() }
            : new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "external-identifier-recorded", from = (string?)null, to = $"{item.GetProperty("externalSystem").GetString()}:{item.GetProperty("identifierType").GetString()}={item.GetProperty("identifier").GetString()}" }).ToArray();
        object? ciSnapshot = snapshot.ValueKind == JsonValueKind.Undefined ? null : new
        {
            commitSha = snapshot.GetProperty("commitSha").GetString(),
            observedAt = snapshot.GetProperty("observedAt").GetString(),
            checks = snapshot.GetProperty("checks").EnumerateArray().GroupBy(check => check.GetProperty("state").GetString()).ToDictionary(group => group.Key!, group => group.Count()),
            workflowRuns = snapshot.GetProperty("workflowRuns").EnumerateArray().Select(run => new { id = run.GetProperty("id").GetString(), state = run.GetProperty("state").GetString() }).ToArray()
        };
        object? ciFailureEvidence = failureEvidence.ValueKind == JsonValueKind.Undefined ? null : new
        {
            commitSha = failureEvidence.GetProperty("commitSha").GetString(),
            providerRunId = failureEvidence.GetProperty("providerRunId").GetString(),
            providerJobId = failureEvidence.GetProperty("providerJobId").GetString(),
            failureClass = failureEvidence.GetProperty("failureClass").GetString(),
            truncated = failureEvidence.GetProperty("truncated").GetBoolean(),
            excerpt = failureEvidence.GetProperty("excerpt").GetString(),
            expansionCommand = $"sdeveng github actions --run-id {failureEvidence.GetProperty("providerRunId").GetString()} --failed-logs"
        };
        var reruns = events.Where(item => item.GetProperty("eventType").GetString() == "ci-rerun").ToArray();
        var currentSignature = failureEvidence.ValueKind == JsonValueKind.Undefined ? null : ComputeCiFailureSignature(
            events.FirstOrDefault(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() == "repository").ValueKind == JsonValueKind.Undefined ? "" : events.First(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() == "repository").GetProperty("identifier").GetString()!,
            failureEvidence.GetProperty("commitSha").GetString()!, failureEvidence.GetProperty("providerRunId").GetString()!, failureEvidence.GetProperty("providerJobId").GetString()!, failureEvidence.GetProperty("failureClass").GetString()!);
        var currentRerunCount = currentSignature is null ? 0 : reruns.Count(item => item.GetProperty("failureSignature").GetString() == currentSignature);
        return new
        {
            kind = "run-explanation",
            runId = runId.ToString("D"),
            eventCount = events.Count,
            timeline,
            ciSnapshot,
            ciFailureEvidence,
            rerunMetrics = new { total = reruns.Length, currentFailureIdentity = currentRerunCount }
        };
    }

    public static JsonElement Resume(string directory, Guid runId) => TransitionCurrent(directory, runId, "paused", "running");

    public static JsonElement Cancel(string directory, Guid runId)
    {
        var events = Read(directory, runId);
        var state = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        var current = state.ValueKind == JsonValueKind.Undefined ? null : state.GetProperty("toState").GetString();
        if (current is null || current is "completed" or "cancelled" or "failed")
            throw new InvalidOperationException("Only an active run can be cancelled.");
        return AppendTransition(directory, runId, current, "cancelled");
    }

    public static JsonElement Abandon(string directory, Guid runId)
    {
        var events = Read(directory, runId);
        var state = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        var current = state.ValueKind == JsonValueKind.Undefined ? null : state.GetProperty("toState").GetString();
        if (current is null or "completed" or "cancelled" or "failed" or "abandoned")
            throw new InvalidOperationException("Only an active run can be abandoned.");
        return AppendTransition(directory, runId, current, "abandoned");
    }

    private static JsonElement TransitionCurrent(string directory, Guid runId, string expected, string next)
    {
        var events = Read(directory, runId);
        var state = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        var current = state.ValueKind == JsonValueKind.Undefined ? null : state.GetProperty("toState").GetString();
        if (current != expected) throw new InvalidOperationException($"Only a {expected} run can be resumed.");
        return AppendTransition(directory, runId, current, next);
    }

    private static JsonElement Append(string directory, Guid runId, string eventType, string? fromState, string? toState,
        string? externalSystem, string? identifierType, string? identifier, string? detail = null)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID must be a UUID.", nameof(runId));
        if (eventType == "state-transition")
        {
            if (string.IsNullOrWhiteSpace(toState) || fromState is not null && string.IsNullOrWhiteSpace(fromState))
                throw new ArgumentException("Transition states must be nonempty.");
        }
        else if (eventType == "operation-completed" && identifier is not ("branch-created" or "branch-pushed"))
            throw new ArgumentException("Unknown operation.");
        else if (eventType != "operation-completed" && eventType != "start-work-progress" && (string.IsNullOrWhiteSpace(externalSystem) || string.IsNullOrWhiteSpace(identifierType) || string.IsNullOrWhiteSpace(identifier)))
            throw new ArgumentException("External identifier fields must be nonempty.");

        var path = Path.Combine(directory, runId.ToString("D"));
        Directory.CreateDirectory(path);
        using var gate = new FileStream(Path.Combine(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var sequence = Read(directory, runId).Count + 1;
        var payload = eventType == "state-transition"
            ? new { schemaVersion = 1, runId = runId.ToString("D"), sequence, occurredAt = DateTimeOffset.UtcNow, eventType, fromState, toState } as object
            : eventType == "operation-completed"
            ? new { schemaVersion = 1, runId = runId.ToString("D"), sequence, occurredAt = DateTimeOffset.UtcNow, eventType, operation = identifier }
            : eventType == "start-work-progress"
            ? new { schemaVersion = 1, runId = runId.ToString("D"), sequence, occurredAt = DateTimeOffset.UtcNow, eventType, progressVersion = 1, operation = identifier, status = identifierType, detail }
            : new { schemaVersion = 1, runId = runId.ToString("D"), sequence, occurredAt = DateTimeOffset.UtcNow, eventType, externalSystem, identifierType, identifier };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var temporary = Path.Combine(path, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(path, sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json"));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
}
