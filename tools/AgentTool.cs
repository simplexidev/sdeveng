#!/usr/bin/env dotnet
#:package Microsoft.Extensions.Hosting@10.0.12
#:property TargetFramework=net10.0
#:property ManagePackageVersionsCentrally=false
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SdevEng;

/// <summary>Performs read-only requests against the GitHub API.</summary>
public interface IGitHubReadClient
{
    Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default);
}

public interface IGitHubLabelProcess
{
    Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd);
}

public sealed class GitHubLabelProcess : IGitHubLabelProcess
{
    public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd) => Processes.Run(executable, arguments, cwd);
}

/// <summary>Production unauthenticated GitHub API read transport.</summary>
public sealed class GitHubReadClient(HttpClient http) : IGitHubReadClient
{
    public async Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is null || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host != "api.github.com")
            throw new ArgumentException("GitHub reads must target the HTTPS api.github.com host.", nameof(endpoint));
        return await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}

public sealed class GitHubTransportException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
    : HttpRequestException(message, innerException, statusCode);

/// <summary>Normalizes GitHub transport and HTTP failures without exposing response bodies.</summary>
public static class GitHubTransport
{
    public static async Task<HttpResponseMessage> GetAsync(IGitHubReadClient client, Uri endpoint, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try { response = await client.GetAsync(endpoint, cancellationToken); }
        catch (HttpRequestException error) { throw new GitHubTransportException("GitHub request failed at the transport layer.", error.StatusCode, error); }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new GitHubTransportException($"GitHub returned HTTP {(int)status} ({status}).", status);
        }
        return response;
    }
}

public sealed record GitHubPrStatusCheck(string Name, string? State, string? Bucket, Uri? Link, string? Workflow, JsonElement Raw);
public sealed record GitHubPrStatus(string? HeadBranch, string? Author, string? ReviewDecision, IReadOnlyList<GitHubPrStatusCheck> Checks, JsonElement Raw);

/// <summary>Reads the fields used by github pr-status from the GitHub pull request API.</summary>
public sealed class GitHubPrStatusReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");
    public async Task<GitHubPrStatus> ReadCurrentBranchAsync(string owner, string repository, string branch, CancellationToken cancellationToken = default)
    {
        static string Part(string value, string name) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant) ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} is invalid.", name);
        var query = Uri.EscapeDataString($"{owner}:{branch}");
        using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/pulls?head={query}&state=open&per_page=100"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub pull request lookup response must be an array.");
        var match = document.RootElement.EnumerateArray().FirstOrDefault(p => string.Equals(Nested(p, "head", "ref"), branch, StringComparison.Ordinal));
        if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty("number", out var numberValue) || !numberValue.TryGetInt32(out var number) || number <= 0)
            throw new InvalidOperationException("No open pull request matches the current branch.");
        return await ReadAsync(owner, repository, number, cancellationToken);
    }
    static string? Nested(JsonElement value, string parent, string child) => value.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(child, out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
    public async Task<GitHubPrStatus> ReadAsync(string owner, string repository, int number, CancellationToken cancellationToken = default)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        static string Part(string value, string name) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} is invalid.", name);
        using var response = await GitHubTransport.GetAsync(client, new Uri(ApiRoot, $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/pulls/{number.ToString(CultureInfo.InvariantCulture)}"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var checks = root.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array
            ? rollup.EnumerateArray().Select(c => new GitHubPrStatusCheck(Text(c, "name") ?? Text(c, "context") ?? "", Text(c, "state") ?? Text(c, "conclusion"), Text(c, "bucket"), Https(Text(c, "link") ?? Text(c, "detailsUrl")), Text(c, "workflow"), c.Clone())).ToArray()
            : Array.Empty<GitHubPrStatusCheck>();
        return new GitHubPrStatus(Nested(root, "head", "ref") ?? Text(root, "headRefName"), Nested(root, "user", "login") ?? Nested(root, "author", "login"), Text(root, "reviewDecision"), checks, root.Clone());
    }
    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static Uri? Https(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
}

public sealed record GitHubReviewComment(long Id, long? ReviewId, string? NodeId, string? Path, int? Position, int? OriginalPosition, string? CommitId, string? OriginalCommitId, string? Author, string? Body, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, Uri? HtmlUrl, Uri? PullRequestUrl, long? InReplyToId, string? AuthorAssociation, int? StartLine, int? OriginalStartLine, string? StartSide, int? Line, int? OriginalLine, string? Side, string? DiffHunk, JsonElement Raw);
public sealed record GitHubReviewCommentPage(IReadOnlyList<GitHubReviewComment> Comments, Uri? NextPage);

/// <summary>Reads one bounded page of inline pull request review comments.</summary>
public sealed class GitHubReviewCommentReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");
    public async Task<GitHubReviewCommentPage> ReadPageAsync(string owner, string repository, int number, Uri? page = null, CancellationToken cancellationToken = default)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        static string Part(string value, string name) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} is invalid.", name);
        var endpoint = page ?? new Uri(ApiRoot, $"repos/{Part(owner, nameof(owner))}/{Part(repository, nameof(repository))}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/comments?per_page=100");
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host != "api.github.com") throw new ArgumentException("Page must target api.github.com.", nameof(page));
        using var response = await GitHubTransport.GetAsync(client, endpoint, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub review comments response must be an array.");
        var comments = document.RootElement.EnumerateArray().Select(Parse).ToArray();
        Uri? next = null;
        if (response.Headers.TryGetValues("Link", out var links)) foreach (var link in links.SelectMany(x => x.Split(',')))
                if (link.Contains("rel=\"next\"", StringComparison.Ordinal) && Uri.TryCreate(link.Split(';')[0].Trim().Trim('<', '>'), UriKind.Absolute, out var parsed)) { next = parsed; break; }
        return new(comments, next);
    }
    static GitHubReviewComment Parse(JsonElement e) => new(Long(e, "id") ?? throw new JsonException("Review comment is missing id."), Long(e, "pull_request_review_id"), Str(e, "node_id"), Str(e, "path"), Int(e, "position"), Int(e, "original_position"), Str(e, "commit_id"), Str(e, "original_commit_id"), Nested(e, "user", "login"), Str(e, "body"), Date(e, "created_at"), Date(e, "updated_at"), Url(e, "html_url"), Url(e, "pull_request_url"), Long(e, "in_reply_to_id"), Str(e, "author_association"), Int(e, "start_line"), Int(e, "original_start_line"), Str(e, "start_side"), Int(e, "line"), Int(e, "original_line"), Str(e, "side"), Str(e, "diff_hunk"), e.Clone());
    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    static long? Long(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.TryGetInt64(out var v) ? v : null;
    static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? v : null;
    static DateTimeOffset? Date(JsonElement e, string n) => DateTimeOffset.TryParse(Str(e, n), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : null;
    static Uri? Url(JsonElement e, string n) => Uri.TryCreate(Str(e, n), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
    static string? Nested(JsonElement e, string p, string n) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? Str(v, n) : null;
}

public sealed record GitHubRepositoryMetadata(
    long Id, string FullName, Uri HtmlUrl, string? Description, string DefaultBranch,
    bool IsPrivate, bool IsArchived, string Visibility, DateTimeOffset? PushedAt, DateTimeOffset? UpdatedAt);

public sealed record GitHubCommitHead(string Sha);
public sealed record GitHubCompareFile(string Filename, string Status, string? PreviousFilename);
public sealed record GitHubCompare(string? HeadCommitSha, IReadOnlyList<GitHubCompareFile> Files);

/// <summary>Reads repository HEAD and bounded compare facts through the shared GitHub read transport.</summary>
public sealed class GitHubCommitReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");

    public async Task<GitHubCommitHead> ReadHeadAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync($"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/commits/HEAD", cancellationToken);
        return new(RequiredString(root, "sha"));
    }

    public async Task<GitHubCompare> CompareToHeadAsync(string owner, string repository, string baseReference, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync($"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/compare/{Segment(baseReference, nameof(baseReference))}...HEAD", cancellationToken);
        var files = root.TryGetProperty("files", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(file => new GitHubCompareFile(RequiredString(file, "filename"), RequiredString(file, "status"), OptionalString(file, "previous_filename"))).ToArray()
            : throw new JsonException("GitHub compare response is missing 'files'.");
        return new(OptionalNestedString(root, "head_commit", "sha"), files);
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
    static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
        ? property.GetString()! : throw new JsonException($"GitHub response is missing '{name}'.");
    static string? OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    static string? OptionalNestedString(JsonElement value, string parent, string name) => value.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object ? OptionalString(nested, name) : null;
}

/// <summary>Reads typed repository metadata through the shared GitHub read transport.</summary>
public sealed class GitHubRepositoryMetadataReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");

    public async Task<GitHubRepositoryMetadata> ReadAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        static string Segment(string value, string name) =>
            !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
                ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} must be a GitHub owner or repository name.", name);

        var endpoint = new Uri(ApiRoot, $"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}");
        using var response = await GitHubTransport.GetAsync(client, endpoint, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        static string RequiredString(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()! : throw new JsonException($"GitHub repository response is missing '{name}'.");
        static bool RequiredBoolean(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean() : throw new JsonException($"GitHub repository response is missing '{name}'.");
        static DateTimeOffset? OptionalDate(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(property.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

        if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var repositoryId) || repositoryId <= 0)
            throw new JsonException("GitHub repository response has an invalid 'id'.");
        if (!Uri.TryCreate(RequiredString(root, "html_url"), UriKind.Absolute, out var htmlUrl) || htmlUrl.Scheme != Uri.UriSchemeHttps)
            throw new JsonException("GitHub repository response has an invalid 'html_url'.");
        var fullName = RequiredString(root, "full_name");
        var visibility = root.TryGetProperty("visibility", out var visibilityValue) && visibilityValue.ValueKind == JsonValueKind.String
            ? visibilityValue.GetString()! : RequiredBoolean(root, "private") ? "private" : "public";
        return new(repositoryId, fullName, htmlUrl, root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null,
            RequiredString(root, "default_branch"), RequiredBoolean(root, "private"), RequiredBoolean(root, "archived"), visibility,
            OptionalDate(root, "pushed_at"), OptionalDate(root, "updated_at"));
    }
}

public sealed record GitHubIssue(
    int Number, string Title, string State, Uri HtmlUrl, string? Body, string? Author);

public sealed record IssueFileReference(string Path, int? StartLine, int? EndLine, bool Exists);
public sealed record IssueSymbolReference(string Value);
public sealed record IssueReferenceFacts(IssueFileReference[] Files, IssueSymbolReference[] Symbols);
public sealed record LinkedIssueReference(string Owner, string Repository, int Number, string Kind);
public sealed record TriageFacts(string? CandidateRepository, IReadOnlyList<LinkedIssueReference> References, bool ReferencesTruncated);
public sealed record IssueTriageFacts(
    string? CandidateRepository, string Title, string Body,
    IReadOnlyList<IssueFileReference> Files, IReadOnlyList<IssueSymbolReference> Symbols,
    IReadOnlyList<LinkedIssueReference> LinkedReferences, IReadOnlyList<string> AreaHints,
    IReadOnlyList<string> UnresolvedFamilies, bool Truncated);

public sealed record TriageLabelCandidate(string Family, string Label, double Confidence, IReadOnlyList<string> Evidence);
public sealed record TriageClassification(IReadOnlyList<TriageLabelCandidate> Candidates, IReadOnlyList<string> UnresolvedFamilies);
public sealed record TriageSelectedLabel(string Label, double Confidence, IReadOnlyList<string> Evidence);
public sealed record TriageSelectedFamily(string Family, IReadOnlyList<TriageSelectedLabel> Labels);
public sealed record TriageDecision(IReadOnlyList<TriageSelectedFamily> Selected, IReadOnlyList<string> UnresolvedFamilies, bool NeedsHumanReview);

public interface ITriageSemanticClassifier
{
    IReadOnlyList<TriageLabelCandidate> Classify(IssueTriageFacts facts, IReadOnlyList<TriageLabelCandidate> deterministicCandidates, JsonElement labelCatalog);
}

/// <summary>Safe semantic-classifier default that makes no model or network calls.</summary>
public sealed class AbstainingTriageSemanticClassifier : ITriageSemanticClassifier
{
    public IReadOnlyList<TriageLabelCandidate> Classify(IssueTriageFacts facts, IReadOnlyList<TriageLabelCandidate> deterministicCandidates, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(deterministicCandidates);
        return Array.Empty<TriageLabelCandidate>();
    }
}

/// <summary>Classifies only explicit issue evidence against the configured label catalog.</summary>
public static class TriageClassifier
{
    public const double MinimumAutomaticConfidence = 0.80;
    static readonly Regex TypeToken = new(@"(?<![A-Za-z0-9_-])type:([A-Za-z0-9_-]+)(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex ExplicitTriageToken = new(@"(?<![A-Za-z0-9_-])(?<family>risk|complexity):(?<value>[A-Za-z0-9_-]+)(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<TriageLabelCandidate> BoundSemanticCandidates(IEnumerable<TriageLabelCandidate> candidates, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = labels.EnumerateArray().Select(item => (Family: item.GetProperty("family").GetString(), Name: item.GetProperty("name").GetString()))
            .ToHashSet();
        return candidates.Where(candidate => configured.Contains((candidate.Family, candidate.Label)) &&
                candidate.Family.Length is > 0 and <= 64 && candidate.Label.Length is > 0 and <= 100 &&
                double.IsFinite(candidate.Confidence) && candidate.Confidence is >= 0 and <= 1)
            .Take(20)
            .Select(candidate => candidate with { Evidence = candidate.Evidence.Take(10).Select(value => value.Length <= 300 ? value : value[..300]).ToArray() })
            .ToArray();
    }

    public static TriageDecision ClassifyDecision(IssueTriageFacts facts, JsonElement labelCatalog, ITriageSemanticClassifier semanticClassifier)
    {
        ArgumentNullException.ThrowIfNull(semanticClassifier);
        var deterministic = Classify(facts, labelCatalog);
        var semantic = semanticClassifier.Classify(facts, deterministic.Candidates, labelCatalog);
        return Combine(deterministic.Candidates, semantic, deterministic.UnresolvedFamilies, labelCatalog);
    }

    public static TriageDecision Combine(IEnumerable<TriageLabelCandidate> deterministicCandidates,
        IEnumerable<TriageLabelCandidate> semanticCandidates, IEnumerable<string> unresolvedFamilies, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(deterministicCandidates);
        ArgumentNullException.ThrowIfNull(semanticCandidates);
        ArgumentNullException.ThrowIfNull(unresolvedFamilies);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = labels.EnumerateArray().Select(item => (Name: item.GetProperty("name").GetString(), Family: item.GetProperty("family").GetString()))
            .Where(item => item.Name is not null && item.Family is not null)
            .ToHashSet();
        var configuredFamilies = configured.Select(item => item.Family!).ToHashSet(StringComparer.Ordinal);
        var exclusiveFamilies = new HashSet<string>(["type", "risk", "complexity", "scope"], StringComparer.Ordinal);
        var candidates = new List<TriageLabelCandidate>();
        var unresolved = new HashSet<string>(unresolvedFamilies.Where(configuredFamilies.Contains), StringComparer.Ordinal);
        var invalidCandidate = false;
        foreach (var (candidate, semantic) in deterministicCandidates.Select(x => (x, false)).Concat(semanticCandidates.Select(x => (x, true))))
        {
            if (candidate is null || !configured.Contains((candidate.Label, candidate.Family)) ||
                !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1 || candidate.Evidence is null)
            {
                invalidCandidate = true;
                if (candidate?.Family is { } invalidFamily && configuredFamilies.Contains(invalidFamily)) unresolved.Add(invalidFamily);
                continue;
            }
            if (semantic && candidate.Confidence < MinimumAutomaticConfidence)
            {
                unresolved.Add(candidate.Family);
                continue;
            }
            candidates.Add(semantic ? candidate : candidate with { Confidence = 1.0 });
        }

        var selected = new List<TriageSelectedFamily>();
        foreach (var family in configuredFamilies.Order(StringComparer.Ordinal))
        {
            var familyCandidates = candidates.Where(candidate => candidate.Family == family)
                .GroupBy(candidate => candidate.Label, StringComparer.Ordinal)
                .Select(group => new TriageLabelCandidate(family, group.Key, group.Max(candidate => candidate.Confidence),
                    group.SelectMany(candidate => candidate.Evidence).Distinct(StringComparer.Ordinal).Take(10).Select(evidence => evidence.Length <= 300 ? evidence : evidence[..300]).ToArray()))
                .OrderBy(candidate => candidate.Label, StringComparer.Ordinal).ToArray();
            if (familyCandidates.Length == 0) { unresolved.Add(family); continue; }
            if (exclusiveFamilies.Contains(family) && familyCandidates.Length > 1) { unresolved.Add(family); continue; }
            selected.Add(new(family, familyCandidates.Select(candidate => new TriageSelectedLabel(candidate.Label, candidate.Confidence, candidate.Evidence)).ToArray()));
            unresolved.Remove(family);
        }
        var unresolvedResult = unresolved.Order(StringComparer.Ordinal).ToArray();
        return new(selected.OrderBy(item => item.Family, StringComparer.Ordinal).ToArray(), unresolvedResult,
            invalidCandidate || unresolvedResult.Length > 0);
    }

    public static TriageClassification Classify(IssueTriageFacts facts, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");

        var configured = labels.EnumerateArray().Select(item =>
        {
            var name = item.GetProperty("name").GetString() ?? throw new JsonException("Configured label name is missing.");
            var family = item.GetProperty("family").GetString() ?? throw new JsonException("Configured label family is missing.");
            return (Name: name, Family: family);
        }).ToArray();
        var candidates = new List<TriageLabelCandidate>();
        var unresolved = facts.UnresolvedFamilies.Distinct(StringComparer.Ordinal).ToList();

        var typeLabels = configured.Where(x => x.Family == "type" && x.Name.StartsWith("type:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var typeEvidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in typeLabels)
        {
            var suffix = label.Name["type:".Length..];
            var prefix = Regex.IsMatch(facts.Title, @"^\s*" + Regex.Escape(suffix) + @"\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var token = TypeToken.Matches(facts.Title + "\n" + facts.Body).Cast<Match>()
                .Any(match => string.Equals(match.Groups[1].Value, suffix, StringComparison.OrdinalIgnoreCase));
            if (prefix || token)
            {
                var evidence = new List<string>();
                if (prefix) evidence.Add("title-prefix:" + suffix + ":");
                if (token) evidence.Add("type-token:" + suffix);
                typeEvidence[label.Name] = evidence;
            }
        }
        if (typeEvidence.Count == 1)
        {
            var pair = typeEvidence.Single();
            candidates.Add(new("type", pair.Key, 1.0, pair.Value));
            unresolved.RemoveAll(x => x == "type");
        }

        var areaLabels = configured.Where(x => x.Family == "area" && x.Name.StartsWith("area:", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var label in areaLabels)
        {
            var suffix = label.Name["area:".Length..];
            var evidence = facts.AreaHints.Where(hint =>
                string.Equals(hint, suffix, StringComparison.OrdinalIgnoreCase) ||
                (hint.StartsWith("area:", StringComparison.OrdinalIgnoreCase) && string.Equals(hint["area:".Length..], suffix, StringComparison.OrdinalIgnoreCase)))
                .Select(hint => "area-hint:" + hint).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (evidence.Length > 0) candidates.Add(new("area", label.Name, 1.0, evidence));
        }
        if (candidates.Any(x => x.Family == "area")) unresolved.RemoveAll(x => x == "area");

        var declaredTokens = ExplicitTriageToken.Matches(facts.Title + "\n" + facts.Body).Cast<Match>().ToArray();
        foreach (var family in new[] { "risk", "complexity" })
        {
            var familyLabels = configured.Where(label => label.Family == family && label.Name.StartsWith(family + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
            var matchingLabels = declaredTokens
                .Where(match => string.Equals(match.Groups["family"].Value, family, StringComparison.OrdinalIgnoreCase))
                .Select(match => familyLabels.FirstOrDefault(label => string.Equals(label.Name[(family.Length + 1)..], match.Groups["value"].Value, StringComparison.OrdinalIgnoreCase)))
                .Where(label => label.Name is not null)
                .DistinctBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matchingLabels.Length == 1)
            {
                var label = matchingLabels[0];
                var evidence = declaredTokens
                    .Where(match => string.Equals(match.Groups["family"].Value, family, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(label.Name[(family.Length + 1)..], match.Groups["value"].Value, StringComparison.OrdinalIgnoreCase))
                    .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                candidates.Add(new(family, label.Name, 1.0, evidence));
                unresolved.RemoveAll(x => x == family);
            }
        }

        var candidateRepository = facts.CandidateRepository;
        if (!string.IsNullOrWhiteSpace(candidateRepository))
        {
            var crossRepository = facts.LinkedReferences.Any(reference =>
                !string.Equals(candidateRepository, reference.Owner + "/" + reference.Repository, StringComparison.OrdinalIgnoreCase));
            var scopeLabel = crossRepository ? "scope:cross-repo" : "scope:single-repo";
            if (configured.Any(label => label.Family == "scope" && string.Equals(label.Name, scopeLabel, StringComparison.OrdinalIgnoreCase)))
            {
                var evidence = crossRepository ? "linked-repository:external" : "linked-repository:local-or-none";
                candidates.Add(new("scope", scopeLabel, 1.0, [evidence]));
                unresolved.RemoveAll(x => x == "scope");
            }
        }
        return new(candidates, unresolved);
    }
}

/// <summary>Extracts explicit issue references and the invocation repository from normalized issue text.</summary>
public static class TriageFactsExtractor
{
    static readonly Regex HttpsReference = new(@"https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/(issues|pull)/([1-9][0-9]*)(?![A-Za-z0-9_/?#.-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex QualifiedReference = new(@"(?<![A-Za-z0-9_./-])([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)#([1-9][0-9]*)(?![0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex LocalReference = new(@"(?<![A-Za-z0-9_./-])#([1-9][0-9]*)(?![0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex AreaHint = new(@"(?<![A-Za-z0-9_-])area:([A-Za-z0-9_.-]+)(?![A-Za-z0-9_.-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static async Task<IssueTriageFacts> ExtractIssueAsync(GitHubIssue issue, string repositoryRoot, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        var text = issue.Title + "\n" + (issue.Body ?? "");
        var references = await ExtractAsync(text, repositoryRoot, maxItems, cancellationToken);
        var sourceFacts = IssueReferenceExtractor.Extract(issue, repositoryRoot, maxItems);
        var files = sourceFacts.Files.Take(maxItems).ToArray();
        var symbols = sourceFacts.Symbols.Take(maxItems).ToArray();
        var areas = new List<string>();
        var areaSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var segment = file.Path.Split('/')[0];
            if (areaSeen.Add(segment)) areas.Add(segment);
        }
        foreach (Match match in AreaHint.Matches(text))
            if (areaSeen.Add(match.Groups[1].Value)) areas.Add("area:" + match.Groups[1].Value);
        var truncated = references.ReferencesTruncated || sourceFacts.Files.Length > maxItems || sourceFacts.Symbols.Length > maxItems || areas.Count > maxItems;
        return new(references.CandidateRepository, issue.Title, issue.Body ?? "", files, symbols,
            references.References.Take(maxItems).ToArray(), areas.Take(maxItems).ToArray(),
            new[] { "type", "area", "risk", "complexity", "scope" }, truncated);
    }

    public static TriageFacts Extract(string normalizedIssueText, string repositoryRoot, int maxItems = 50)
    {
        ArgumentNullException.ThrowIfNull(normalizedIssueText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        return ExtractAsync(normalizedIssueText, repositoryRoot, maxItems).GetAwaiter().GetResult();
    }

    public static async Task<TriageFacts> ExtractAsync(string normalizedIssueText, string repositoryRoot, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedIssueText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        string? candidate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Processes.Run("git", ["remote", "get-url", "--all", "origin"], Path.GetFullPath(repositoryRoot), timeout: TimeSpan.FromSeconds(5));
            var urls = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (result.ExitCode != 0 || urls.Length != 1) candidate = null;
            else
            {
                var target = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(urls[0]);
                candidate = target.Owner + "/" + target.Repository;
            }
        }
        catch { candidate = null; }
        return ExtractReferences(normalizedIssueText, candidate, maxItems);
    }

    static TriageFacts ExtractReferences(string text, string? candidate, int maxItems)
    {
        var found = new List<(int Index, LinkedIssueReference Reference)>();
        foreach (Match match in HttpsReference.Matches(text))
            if (int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                found.Add((match.Index, new(match.Groups[1].Value, match.Groups[2].Value, number, match.Groups[3].Value.Equals("pull", StringComparison.OrdinalIgnoreCase) ? "pull-request" : "issue")));
        foreach (Match match in QualifiedReference.Matches(text))
            if (int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                found.Add((match.Index, new(match.Groups[1].Value, match.Groups[2].Value, number, "issue-or-pr")));
        if (candidate is not null)
        {
            var parts = candidate.Split('/');
            foreach (Match match in LocalReference.Matches(text))
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    found.Add((match.Index, new(parts[0], parts[1], number, "issue-or-pr")));
        }
        var references = new List<LinkedIssueReference>();
        var seen = new HashSet<LinkedIssueReference>();
        foreach (var item in found.OrderBy(item => item.Index)) if (seen.Add(item.Reference)) references.Add(item.Reference);
        return new(candidate, references.Take(maxItems).ToArray(), references.Count > maxItems);
    }
}

public sealed record GitHubPullRequest(
    int Number, string Title, string State, Uri HtmlUrl, string? Body, string? Author,
    string BaseBranch, string HeadBranch, bool IsDraft, bool IsMerged);

/// <summary>Reads typed issue and pull request data through the shared GitHub read transport.</summary>
public sealed class GitHubIssueReader(IGitHubReadClient client)
{
    static readonly Uri ApiRoot = new("https://api.github.com/");

    public async Task<GitHubIssue> ReadIssueAsync(string owner, string repository, int number, CancellationToken cancellationToken = default) =>
        ToIssue(await ReadAsync(owner, repository, number, false, cancellationToken));

    public async Task<IReadOnlyList<string>> ReadIssueLabelNamesAsync(string owner, string repository, int number, CancellationToken cancellationToken = default)
    {
        var root = await ReadAsync(owner, repository, number, false, cancellationToken);
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("GitHub issue response is missing 'labels'.");
        return labels.EnumerateArray().Select(label =>
            label.ValueKind == JsonValueKind.Object && label.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()! : throw new JsonException("GitHub issue response contains an invalid label name."))
            .Take(100).ToArray();
    }

    public async Task<GitHubPullRequest> ReadPullRequestAsync(string owner, string repository, int number, CancellationToken cancellationToken = default) =>
        ToPullRequest(await ReadAsync(owner, repository, number, true, cancellationToken));

    async Task<JsonElement> ReadAsync(string owner, string repository, int number, bool pullRequest, CancellationToken cancellationToken)
    {
        static string Segment(string value, string name) =>
            !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
                ? Uri.EscapeDataString(value) : throw new ArgumentException($"{name} must be a GitHub owner or repository name.", name);
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number), "GitHub issue or pull request number must be positive.");
        var resource = pullRequest ? "pulls" : "issues";
        var endpoint = new Uri(ApiRoot, $"repos/{Segment(owner, nameof(owner))}/{Segment(repository, nameof(repository))}/{resource}/{number.ToString(CultureInfo.InvariantCulture)}");
        using var response = await GitHubTransport.GetAsync(client, endpoint, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    static GitHubIssue ToIssue(JsonElement root) =>
        new(Number(root), NormalizeIssueText(String(root, "title"))!, String(root, "state"), Url(root), NormalizeIssueText(OptionalString(root, "body")), Author(root));

    static string? NormalizeIssueText(string? value) => value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    static GitHubPullRequest ToPullRequest(JsonElement root)
    {
        var number = Number(root);
        var title = String(root, "title");
        var state = String(root, "state");
        var url = Url(root);
        var body = OptionalString(root, "body");
        var author = Author(root);
        var baseBranch = NestedString(root, "base", "ref");
        var headBranch = NestedString(root, "head", "ref");
        var draft = RequiredBoolean(root, "draft");
        var merged = RequiredBoolean(root, "merged");
        return new(number, title, state, url, body, author, baseBranch, headBranch, draft, merged);
    }

    static int Number(JsonElement root) => root.TryGetProperty("number", out var value) && value.TryGetInt32(out var number) && number > 0
        ? number : throw new JsonException("GitHub response has an invalid 'number'.");
    static string String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new JsonException($"GitHub response is missing '{name}'.");
    static string? OptionalString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string? Author(JsonElement root) => root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? OptionalString(user, "login") : null;
    static string NestedString(JsonElement root, string parent, string name) => root.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object
        ? String(value, name) : throw new JsonException($"GitHub response is missing '{parent}.{name}'.");
    static bool RequiredBoolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new JsonException($"GitHub response is missing '{name}'.");
    static Uri Url(JsonElement root) => Uri.TryCreate(String(root, "html_url"), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        ? url : throw new JsonException("GitHub response has an invalid 'html_url'.");
}

/// <summary>Extracts explicit repository file and C# symbol references from normalized issue text.</summary>
public static class IssueReferenceExtractor
{
    static readonly Regex InlineCode = new(@"(?<!`)`([^`\r\n]+)`(?!`)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex MarkdownLink = new(@"\[[^\]\r\n]*\]\(([^\s)]+)(?:\s+[^)]*)?\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Symbol = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\(\))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex LineSuffix = new(@"(?::([1-9][0-9]*)(?:-([1-9][0-9]*))?|#L([1-9][0-9]*)(?:-L([1-9][0-9]*))?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IssueReferenceFacts Extract(GitHubIssue issue, string repositoryRoot, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        var root = Path.GetFullPath(repositoryRoot);
        var text = issue.Title + "\n" + (issue.Body ?? "");
        var spans = InlineCode.Matches(text).Cast<Match>().Select(match => (Match: match, Value: match.Groups[1].Value)).ToArray();
        var files = new List<IssueFileReference>();
        var symbols = new List<IssueSymbolReference>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        void AddFile(string raw)
        {
            if (files.Count >= maxItems || !TryFile(raw, out var path, out var start, out var end) || !seenFiles.Add(path)) return;
            var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
            files.Add(new(path, start, end, File.Exists(fullPath) || Directory.Exists(fullPath)));
        }
        var references = spans.Select(item => (item.Match.Index, item.Value, IsCode: true))
            .Concat(MarkdownLink.Matches(text).Cast<Match>().Select(match => (match.Index, match.Groups[1].Value, IsCode: false)))
            .OrderBy(item => item.Index);
        foreach (var item in references)
        {
            if (TryFile(item.Value, out _, out _, out _) && (!Symbol.IsMatch(item.Value) || HasFileExtension(item.Value))) { AddFile(item.Value); continue; }
            if (item.IsCode && symbols.Count < maxItems && Symbol.IsMatch(item.Value) && seenSymbols.Add(item.Value)) symbols.Add(new(item.Value));
        }
        return new(files.ToArray(), symbols.ToArray());
    }

    static bool HasFileExtension(string value) => Regex.IsMatch(value, @"\.(?:cs|csx|md|json|ya?ml|txt|xml|html|css|js|ts|sh|ps1|sln|csproj|props|targets)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool TryFile(string raw, out string path, out int? start, out int? end)
    {
        path = ""; start = end = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains(' ') || Uri.TryCreate(raw, UriKind.Absolute, out _)) return false;
        var value = raw.Replace('\\', '/');
        var suffix = LineSuffix.Match(value);
        if (suffix.Success)
        {
            var first = suffix.Groups[1].Success ? suffix.Groups[1] : suffix.Groups[3];
            var last = suffix.Groups[2].Success ? suffix.Groups[2] : suffix.Groups[4];
            if (!int.TryParse(first.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var firstLine) || (last.Success && !int.TryParse(last.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
            start = firstLine;
            end = last.Success ? int.Parse(last.Value, CultureInfo.InvariantCulture) : firstLine;
            if (end < start) return false;
            value = value[..suffix.Index];
        }
        if (value.Length == 0 || value.Contains(':') || value.Contains('#') || value.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(value, @"^[A-Za-z]:") || value.Split('/').Any(part => part is "" or "." or "..")) return false;
        if (!value.Contains('/') && !Path.HasExtension(value)) return false;
        path = value;
        return true;
    }
}

public sealed record GitHubCheck(int Id, string Name, string Status, string? Conclusion, Uri? DetailsUrl);
public sealed record GitHubWorkflow(long Id, string Name, string State, Uri HtmlUrl);
public sealed record GitHubWorkflowRun(long Id, string Name, string Status, string? Conclusion, Uri HtmlUrl);

public sealed record GitHubActionRun(long Id, string Workflow, string? Title, string Status, string? Conclusion, string Event, string? Branch, string? Sha, Uri Url, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);
public sealed record GitHubActionStep(string? Name, int? Number, string? Conclusion);
public sealed record GitHubActionJob(long Id, string? Name, string? Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Uri? Url, IReadOnlyList<GitHubActionStep> FailedSteps);
public sealed record GitHubActionRunDetail(GitHubActionRun Run, IReadOnlyList<GitHubActionJob> Jobs);
public sealed record GitHubActionFailedLog(long JobId, string? JobName, string Log);

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
            output.Add(new(job.Id, job.Name, new string(buffer, 0, length) + (truncated ? marker : "")));
            remaining -= length + (truncated ? marker.Length : 0);
        }
        return output;
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

public interface IGitHubCredentialProvider
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the active GitHub CLI token into memory for an authenticated API request.</summary>
public sealed class GitHubCredentialProvider : IGitHubCredentialProvider
{
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Processes.Run("gh", ["auth", "token"], Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(10));
            if (result.ExitCode != 0) return null;
            var token = result.Output.Trim();
            return token.Length > 0 && !token.Any(char.IsControl) ? token : null;
        }
        catch { return null; }
    }
}

/// <summary>Performs mutations against the GitHub API.</summary>
public interface IGitHubWriteClient
{
    Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default);
}

/// <summary>Restricts GitHub writes to explicitly owned pull request creation and editing.</summary>
public sealed class GitHubWriteClient(HttpClient http, IGitHubCredentialProvider credentials) : IGitHubWriteClient
{
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        if (method is null || endpoint is null || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Host != "api.github.com" || endpoint.Port != 443 ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("GitHub writes require an exact HTTPS API endpoint.", nameof(endpoint));

        var parts = endpoint.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var allowed = parts.Length >= 4 && parts[0] == "repos" &&
            parts[1].Length > 0 && parts[2].Length > 0 && parts[3] == "pulls" &&
            (method == HttpMethod.Post && parts.Length == 4 ||
             method == HttpMethod.Patch && parts.Length == 5 && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) ||
            parts.Length == 6 && parts[0] == "repos" && parts[1].Length > 0 && parts[2].Length > 0 &&
            parts[3] == "issues" && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var issueNumber) && issueNumber > 0 &&
            parts[5] == "labels" && method == HttpMethod.Post;
        if (!allowed) throw new InvalidOperationException("GitHub mutation is not allowlisted.");
        string? token;
        try { token = await credentials.GetTokenAsync(cancellationToken); }
        catch { token = null; }
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new InvalidOperationException("GitHub authentication is unavailable.");

        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("sdeveng");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try { return await http.SendAsync(request, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HttpRequestException("GitHub mutation request failed.");
        }
    }
}

public sealed record GitHubIssueLabelWriteResult(string Status, string TargetRepository, int IssueNumber, IReadOnlyList<string> Labels, string? Reason);

/// <summary>Adds selected configured area, risk, and complexity labels to the canonical origin issue without replacing existing labels.</summary>
public sealed class GitHubIssueLabelWriter(IGitHubWriteClient client, GitHubIssueReader issueReader)
{
    public async Task<GitHubIssueLabelWriteResult> AddTriageLabelsAsync(
        string currentOriginRepository, string candidateRepository, int issueNumber, TriageDecision decision,
        AgentTool.GitHubCapabilities capabilities, JsonElement labelCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        if (!IsRepository(currentOriginRepository) || !StringComparer.Ordinal.Equals(candidateRepository, currentOriginRepository))
            return Reject("review", candidateRepository, issueNumber, "candidate repository does not match canonical origin");
        var capability = capabilities.Capabilities.SingleOrDefault(item => item.Operation == "issues.labels.write" && item.TargetRepository == currentOriginRepository);
        if (capability?.State != "allowed")
            return Reject("review", currentOriginRepository, issueNumber, "issue label write capability is not allowed");
        var repositoryParts = currentOriginRepository.Split('/', 2);
        var owner = repositoryParts[0];
        var repository = repositoryParts[1];
        var currentLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
        if (currentLabels.Contains("IN_PROGRESS", StringComparer.Ordinal))
            return Reject("review", currentOriginRepository, issueNumber, "issue is already in progress; triage cannot proceed");
        if (!labelCatalog.TryGetProperty("labels", out var configuredLabels) || configuredLabels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = configuredLabels.EnumerateArray().Select(item =>
            (Name: item.GetProperty("name").GetString(), Family: item.GetProperty("family").GetString())).ToArray();
        var selectedLabels = new List<string>();
        foreach (var family in new[] { "area", "risk", "complexity" })
        {
            var familySelections = decision.Selected.Where(item => item.Family == family).ToArray();
            if (decision.UnresolvedFamilies.Contains(family, StringComparer.Ordinal)) continue;
            if (familySelections.Length > 1 || familySelections.Any(item => item.Labels.Count > (family == "area" ? 20 : 1)))
                return Reject("failure", currentOriginRepository, issueNumber, $"selected {family} labels exceed family cardinality");
            var familyLabels = familySelections.SelectMany(item => item.Labels).Select(item => item.Label).Distinct(StringComparer.Ordinal).ToArray();
            if (familyLabels.Any(label => !configured.Any(item => item.Name == label && item.Family == family)))
                return Reject("failure", currentOriginRepository, issueNumber, $"selected {family} label is not configured");
            selectedLabels.AddRange(familyLabels);
        }
        if (selectedLabels.Count == 0)
        {
            var verifiedLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
            return verifiedLabels.Contains("IN_PROGRESS", StringComparer.Ordinal)
                ? Reject("review", currentOriginRepository, issueNumber, "issue became in progress during triage")
                : Reject("review", currentOriginRepository, issueNumber, "no applicable label was selected; human handling is required");
        }
        var labels = selectedLabels.Distinct(StringComparer.Ordinal).ToArray();
        using var content = new StringContent(JsonSerializer.Serialize(new { labels }));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var response = await client.SendAsync(HttpMethod.Post,
            new Uri($"https://api.github.com/repos/{currentOriginRepository}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}/labels"), content, cancellationToken);
        using (response)
        {
            if (!response.IsSuccessStatusCode) return Reject("failure", currentOriginRepository, issueNumber, "GitHub rejected issue label addition");
        }
        var postconditionLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
        if (postconditionLabels.Contains("IN_PROGRESS", StringComparer.Ordinal))
            return Reject("review", currentOriginRepository, issueNumber, "issue became in progress during triage");
        return new("applied", currentOriginRepository, issueNumber, labels, null);
    }

    static bool IsRepository(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    static GitHubIssueLabelWriteResult Reject(string status, string? repository, int issue, string reason) =>
        new(status, repository ?? string.Empty, issue, Array.Empty<string>(), reason);
}

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
        "bootstrap-created" or "pr-created" or "pr-linked" or "metadata-persisted";

    public static JsonElement AppendExternalIdentifier(string directory, Guid runId, string externalSystem, string identifierType, string identifier) =>
        Append(directory, runId, "external-identifier-recorded", null, null, externalSystem, identifierType, identifier);

    public static JsonElement AppendRepositoryIdentifier(string directory, Guid runId, string repository) =>
        AppendExternalIdentifier(directory, runId, "git", "repository", repository);

    public static JsonElement AppendBranchIdentifier(string directory, Guid runId, string branch) =>
        AppendExternalIdentifier(directory, runId, "git", "branch", branch);

    public static JsonElement AppendIssueIdentifier(string directory, Guid runId, string issue) =>
        AppendExternalIdentifier(directory, runId, "github", "issue", issue);

    public static JsonElement AppendPullRequestIdentifier(string directory, Guid runId, string pullRequest) =>
        AppendExternalIdentifier(directory, runId, "github", "pull-request", pullRequest);

    public static JsonElement AppendStepCommitIdentifier(string directory, Guid runId, string commit) =>
        AppendExternalIdentifier(directory, runId, "git", "step-commit", commit);

    public static JsonElement AppendCiRunIdentifier(string directory, Guid runId, string ciRun) =>
        AppendExternalIdentifier(directory, runId, "github-actions", "ci-run", ciRun);

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
        var timeline = events.Select(item => item.GetProperty("eventType").GetString() == "start-work-progress"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "start-work-progress", from = item.GetProperty("operation").GetString(), to = item.GetProperty("status").GetString() }
            : item.GetProperty("eventType").GetString() == "operation-completed"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "operation-completed", from = (string?)null, to = item.GetProperty("operation").GetString() }
            : item.GetProperty("eventType").GetString() == "state-transition"
            ? (object)new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "state-transition", from = item.GetProperty("fromState").GetString(), to = item.GetProperty("toState").GetString() }
            : new { sequence = item.GetProperty("sequence").GetInt32(), occurredAt = item.GetProperty("occurredAt").GetString(), type = "external-identifier-recorded", from = (string?)null, to = $"{item.GetProperty("externalSystem").GetString()}:{item.GetProperty("identifierType").GetString()}={item.GetProperty("identifier").GetString()}" }).ToArray();
        return new { kind = "run-explanation", runId = runId.ToString("D"), eventCount = events.Count, timeline };
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

public sealed record StartWorkRequest(Guid ProductRunId, string RepositoryRoot, int SourceIssueNumber,
    string BaseRef, string ExpectedBaseSha, string BranchName);

public sealed record StartWorkResult(Guid ProductRunId, string Repository, int SourceIssueNumber,
    string BaseSha, string BranchName, string State);

public interface IStartWorkGitProcess
{
    Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd);
}

public sealed class StartWorkGitProcess : IStartWorkGitProcess
{
    public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd) => Processes.Run(executable, arguments, cwd);
}

/// <summary>Checks the chosen start-work target before recording its durable STARTING state.</summary>
public sealed class StartWorkCoordinator(GitHubIssueReader issueReader, AgentTool.GitHubAuthorizationProbe authorizationProbe, IStartWorkGitProcess gitProcess, IGitHubWriteClient writeClient)
{
    public async Task<string> BootstrapAsync(string repositoryRoot, Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID must be a UUID.", nameof(runId));
        var root = Path.GetFullPath(repositoryRoot);
        var state = await Git.State(root);
        if (state.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
        var before = (await Git.Require(root, "rev-parse", "HEAD")).Trim();
        var runDirectory = Path.Combine(root, ".sdeveng", "runs");
        var result = await gitProcess.Run("git", ["-c", "user.name=sdeveng", "-c", "user.email=sdeveng@localhost", "commit", "--allow-empty", "-m", "chore: initialize sdeveng work"], root);
        string mode;
        string resultValue;
        if (result.ExitCode == 0)
        {
            mode = "empty-commit";
            resultValue = (await Git.Require(root, "rev-parse", "HEAD")).Trim();
        }
        else
        {
            var after = (await Git.Require(root, "rev-parse", "HEAD")).Trim();
            if (after != before) throw new InvalidOperationException("Empty-commit command failed after changing HEAD; refusing marker fallback.");
            var relativeMarker = ".sdeveng/bootstrap/" + runId.ToString("D") + ".json";
            var marker = Path.Combine(root, relativeMarker.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var contents = JsonSerializer.Serialize(new { schemaVersion = 1, runId = runId.ToString("D"), purpose = "draft-pr-bootstrap" }, AgentTool.Json) + "\n";
            if (File.Exists(marker) && File.ReadAllText(marker) != contents)
                throw new InvalidOperationException("Bootstrap marker is not owned by this run.");
            await File.WriteAllTextAsync(marker, contents, new UTF8Encoding(false), cancellationToken);
            var add = await gitProcess.Run("git", ["add", "-f", "--", relativeMarker], root);
            if (add.ExitCode != 0) throw new IOException("Unable to stage the owned bootstrap marker: " + Secrets.Redact(add.Output.Trim()));
            var commit = await gitProcess.Run("git", ["-c", "user.name=sdeveng", "-c", "user.email=sdeveng@localhost", "commit", "--only", "-m", "chore: initialize sdeveng work", "--", relativeMarker], root);
            if (commit.ExitCode != 0) throw new IOException("Unable to commit the owned bootstrap marker: " + Secrets.Redact(commit.Output.Trim()));
            mode = "marker";
            resultValue = (await Git.Require(root, "rev-parse", "HEAD")).Trim();
        }
        LocalRunEventStore.AppendStartWorkProgress(runDirectory, runId, "bootstrap-created", "completed", mode);
        return resultValue;
    }

    public async Task<StartWorkResult> ContinueAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        var directory = Path.Combine(root, ".sdeveng", "runs");
        var events = LocalRunEventStore.Read(directory, request.ProductRunId);
        var stateEvent = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        var currentState = stateEvent.ValueKind == JsonValueKind.Undefined ? null : stateEvent.GetProperty("toState").GetString();
        if (currentState is not ("STARTING" or "STARTING_RETRYABLE"))
            throw new InvalidOperationException("Branch push requires a validated STARTING run.");
        var activeOperation = events.Any(item => item.GetProperty("eventType").GetString() is "start-work-progress" or "operation-completed" &&
            item.GetProperty("operation").GetString() == "branch-created" &&
            (item.GetProperty("eventType").GetString() == "operation-completed" || item.GetProperty("status").GetString() == "completed"))
            ? "branch-pushed" : "branch-created";
        try
        {
            await Git.Require(root, "check-ref-format", "--branch", request.BranchName);
            var baseSha = (await Git.Require(root, "rev-parse", "--verify", "--end-of-options", request.BaseRef + "^{commit}")).Trim();
            if (!string.Equals(baseSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected base SHA is stale.");
            var origin = (await Git.Require(root, "remote", "get-url", "--all", "origin"))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (origin.Length != 1) throw new InvalidOperationException("Canonical origin must have one GitHub URL.");
            var (owner, repository) = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(origin[0]);
            var target = $"{owner}/{repository}";
            bool HasIdentifier(string system, string type, string value) => events.Any(item =>
                item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("externalSystem").GetString() == system && item.GetProperty("identifierType").GetString() == type && item.GetProperty("identifier").GetString() == value);
            if (!HasIdentifier("git", "repository", target) || !HasIdentifier("github", "issue", request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("STARTING target does not match its recorded identifiers.");
            if (events.Any(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("identifierType").GetString() == "branch" && item.GetProperty("identifier").GetString() != request.BranchName))
                throw new InvalidOperationException("STARTING run already owns a different branch.");
            if (currentState == "STARTING_RETRYABLE")
                LocalRunEventStore.AppendTransition(directory, request.ProductRunId, currentState, "STARTING");
            bool Completed(string operation) => events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == operation && item.GetProperty("status").GetString() == "completed") ||
                events.Any(item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == operation);
            var gitState = await Git.State(root);
            if (gitState.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
            var branchRef = "refs/heads/" + request.BranchName;
            var branch = await Processes.Run("git", ["rev-parse", "--verify", branchRef], root);
            if (branch.ExitCode == 0)
            {
                var bootstrapRecorded = events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" &&
                    item.GetProperty("operation").GetString() == "bootstrap-created" && item.GetProperty("status").GetString() == "completed");
                var validBootstrapBranch = bootstrapRecorded &&
                    (await Git.Require(root, "rev-list", "--count", baseSha + ".." + branchRef)).Trim() == "1" &&
                    (await Processes.Run("git", ["merge-base", "--is-ancestor", baseSha, branchRef], root)).ExitCode == 0;
                if ((branch.Output.Trim() != baseSha && !validBootstrapBranch) ||
                    (await Git.Require(root, "config", "--get", "branch." + request.BranchName + "." + GitOwnershipMarkers.BranchConfigKey)).Trim() != GitOwnershipMarkers.BranchConfigValue)
                    throw new InvalidOperationException("Existing branch is not the owned branch at the verified base.");
            }
            else
            {
                if (Completed("branch-created")) throw new InvalidOperationException("Recorded owned branch is missing.");
                if (gitState.Head != baseSha) throw new InvalidOperationException("HEAD must match the verified base.");
                var changes = await Git.Require(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".", ":(exclude).sdeveng/runs");
                if (changes.Length != 0) throw new InvalidOperationException("Worktree has unrelated changes.");
                await Git.Require(root, "branch", request.BranchName, baseSha);
                await Git.Require(root, "config", "branch." + request.BranchName + "." + GitOwnershipMarkers.BranchConfigKey, GitOwnershipMarkers.BranchConfigValue);
            }
            await Git.Require(root, "checkout", request.BranchName);
            if ((await Git.Require(root, "rev-list", "--count", baseSha + ".." + branchRef)).Trim() == "0")
            {
                await BootstrapAsync(root, request.ProductRunId, cancellationToken);
                branch = await Processes.Run("git", ["rev-parse", "--verify", branchRef], root);
                if (branch.ExitCode != 0) throw new InvalidOperationException("Bootstrap did not create the owned branch commit.");
            }
            if (!HasIdentifier("git", "branch", request.BranchName)) LocalRunEventStore.AppendBranchIdentifier(directory, request.ProductRunId, request.BranchName);
            if (!Completed("branch-created")) LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "branch-created", "completed");
            activeOperation = "branch-pushed";
            if (!Completed("branch-pushed"))
            {
                var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                if (!capabilities.Capabilities.Any(item => item.Operation == "branch-push" && item.TargetRepository == target && item.State == "allowed"))
                    throw new InvalidOperationException("Branch push capability is not allowed.");
                var pushUrl = (await Git.Require(root, "remote", "get-url", "--push", "origin")).Trim();
                var remoteBranch = await Processes.Run("git", ["ls-remote", "--heads", pushUrl, branchRef], root);
                if (remoteBranch.ExitCode != 0) throw new IOException("Remote branch inspection failed.");
                var remoteSha = remoteBranch.Output.Split('\t')[0].Trim();
                if (remoteSha.Length != 0 && remoteSha != baseSha)
                    throw new InvalidOperationException("Remote branch differs from the owned base; preserving it for recovery.");
                if (remoteSha.Length == 0)
                {
                    var push = await Processes.Run("git", ["push", "--", "origin", branchRef + ":" + branchRef], root);
                    if (push.ExitCode != 0) throw new IOException("Branch push failed; the owned local branch remains available: " + Secrets.Redact(push.Output.Trim()));
                }
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "branch-pushed", "completed");
            }
            activeOperation = "pr-created";
            var persistedPr = events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("externalSystem").GetString() == "github" && item.GetProperty("identifierType").GetString() == "pull-request")
                .Select(item => item.GetProperty("identifier").GetString()).LastOrDefault();
            if (Completed("pr-created") || persistedPr is not null)
            {
                if (persistedPr is null || !int.TryParse(persistedPr, NumberStyles.None, CultureInfo.InvariantCulture, out var existingNumber))
                    throw new InvalidOperationException("Completed pull request progress has no valid identifier.");
                var existing = await issueReader.ReadPullRequestAsync(owner, repository, existingNumber, cancellationToken);
                if (existing.HeadBranch != request.BranchName || existing.BaseBranch != request.BaseRef || !existing.IsDraft ||
                    existing.Body?.Contains($"<!-- sdeveng-run:{request.ProductRunId:D} -->", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Persisted pull request does not match this run.");
            }
            else
            {
                var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                if (!capabilities.Capabilities.Any(item => item.Operation == "pr-create" && item.TargetRepository == target && item.State == "allowed"))
                    throw new InvalidOperationException("Pull request creation capability is not allowed.");
                var issue = await issueReader.ReadIssueAsync(owner, repository, request.SourceIssueNumber, cancellationToken);
                var title = issue.Title.Trim();
                if (title.Length > 256) title = title[..256];
                if (title.Length == 0) throw new InvalidOperationException("Source issue title is empty.");
                var body = $"<!-- sdeveng-run:{request.ProductRunId:D} -->";
                using var content = new StringContent(JsonSerializer.Serialize(new { title, head = request.BranchName, @base = request.BaseRef, draft = true, body }));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await writeClient.SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{target}/pulls"), content, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub rejected draft pull request creation.");
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var number = document.RootElement.GetProperty("number").GetInt32();
                if (number <= 0) throw new JsonException("GitHub returned an invalid pull request number.");
                persistedPr = number.ToString(CultureInfo.InvariantCulture);
                LocalRunEventStore.AppendPullRequestIdentifier(directory, request.ProductRunId, persistedPr);
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "pr-created", "completed", persistedPr);
            }
            return new(request.ProductRunId, target, request.SourceIssueNumber, baseSha, request.BranchName, "STARTING");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var progress = LocalRunEventStore.Read(directory, request.ProductRunId);
            if (progress.Any(item => item.GetProperty("eventType").GetString() == "operation-completed" ||
                item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("status").GetString() == "completed"))
            {
                var terminal = error is InvalidOperationException;
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, activeOperation, terminal ? "terminal-failure" : "retryable-failure", error.Message);
                var latest = progress.Last(item => item.GetProperty("eventType").GetString() == "state-transition").GetProperty("toState").GetString();
                LocalRunEventStore.AppendTransition(directory, request.ProductRunId, latest, terminal ? "STARTING_FAILED" : "STARTING_RETRYABLE");
            }
            throw;
        }
    }

    public async Task<StartWorkResult> StartAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProductRunId == Guid.Empty) throw new ArgumentException("Product run ID must be a UUID.", nameof(request));
        if (request.SourceIssueNumber <= 0) throw new ArgumentException("Source issue number must be positive.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RepositoryRoot)) throw new ArgumentException("Repository root is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BranchName) || request.BranchName.StartsWith('-')) throw new ArgumentException("Branch name is invalid.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BaseRef) || request.BaseRef.StartsWith('-')) throw new ArgumentException("Base ref is invalid.", nameof(request));
        if (!Regex.IsMatch(request.ExpectedBaseSha ?? "", @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
            throw new ArgumentException("Expected base SHA must be a full commit ID.", nameof(request));

        var root = Path.GetFullPath(request.RepositoryRoot);
        var state = await Git.State(root);
        if (!string.Equals(state.Root, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Repository root must be the Git worktree root.");
        if (state.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
        // Product run events live in the worktree; exclude only that store from the clean check.
        var userChanges = await Git.Require(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".", ":(exclude).sdeveng/runs");
        if (userChanges.Length != 0) throw new InvalidOperationException("Worktree has changes outside the product run store.");
        await Git.Require(root, "check-ref-format", "--branch", request.BranchName);
        await Git.Require(root, "check-ref-format", "--branch", request.BaseRef);
        var origin = await Git.Require(root, "remote", "get-url", "--all", "origin");
        var urls = origin.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length != 1) throw new InvalidOperationException("Canonical origin must have one GitHub URL.");
        var (owner, repository) = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(urls[0]);
        var actualSha = (await Git.Require(root, "rev-parse", "--verify", "--end-of-options", request.BaseRef + "^{commit}")).Trim();
        if (!string.Equals(actualSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected base SHA is stale.");
        var issue = await issueReader.ReadIssueAsync(owner, repository, request.SourceIssueNumber, cancellationToken);
        var expectedIssueUrl = $"https://github.com/{owner}/{repository}/issues/{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)}";
        if (issue.Number != request.SourceIssueNumber || !string.Equals(issue.HtmlUrl.AbsoluteUri, expectedIssueUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source issue does not belong to canonical origin.");

        var runDirectory = Path.Combine(root, ".sdeveng", "runs");
        var events = LocalRunEventStore.Read(runDirectory, request.ProductRunId);
        var transition = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        if (transition.ValueKind == JsonValueKind.Undefined || transition.GetProperty("toState").GetString() != "created")
            throw new InvalidOperationException("Start work requires an existing created run.");
        LocalRunEventStore.AppendRepositoryIdentifier(runDirectory, request.ProductRunId, $"{owner}/{repository}");
        LocalRunEventStore.AppendIssueIdentifier(runDirectory, request.ProductRunId, request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture));
        LocalRunEventStore.AppendTransition(runDirectory, request.ProductRunId, "created", "STARTING");
        return new(request.ProductRunId, $"{owner}/{repository}", request.SourceIssueNumber, actualSha, request.BranchName, "STARTING");
    }
}

public static class AgentTool
{
    public const string Product = "sdeveng";
    public const string CliVersion = "3.0.0";
    public const int ResultSchemaVersion = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public const string Help = """
        sdeveng — SimplexiDev Engineering Toolkit deterministic → JEV → Codex
        sdeveng <command> [options]

        version | --version
        config explain
        install | update [--home DIR] [--codex-home DIR] [--dry-run] [--bin]
        uninstall [--home DIR] [--codex-home DIR] [--dry-run]
        doctor
        repo changed-files [--base REF] | summary [--base REF] | locate --query TEXT | health | hygiene
        repo affected-projects [--base REF] | ownership --file PATH
        git state | summary [--base REF] | conflict-forecast --base REF | prepare-commit
        git stage-owned --paths-file FILE | commit-owned --paths-file FILE --message TEXT
        git issue-start --issue NUMBER --branch NAME
        git branch-create --branch NAME | push-owned --remote NAME --branch NAME
        git worktree-create --branch NAME --path DIR | worktree-remove-owned --path DIR
        git stale-base --base REF --expected SHA | abandon-owned --branch NAME --path DIR
        github pr-status | review-comments --pr NUMBER | prepare-pr | labels [--dry-run | --apply]
        github actions [--run-id NUMBER] [--failed-logs]
        dotnet inspect [--project PATH] | build-plan [--base REF] [--project PATH] [--configuration NAME] [--binlog]
        dotnet test-plan [--base REF] [--project PATH] [--configuration NAME]
            [--test NAME | --class NAME | --category NAME | --filter EXPR]
        dotnet diagnostics-plan [--process-id NUMBER] [--signal counters|cpu|contention|allocations|managed-memory|crash|hang]
            [--duration-seconds NUMBER]
        dotnet verify [--base REF] [--project PATH] | format --project PATH [--apply]
        dotnet dependencies --project PATH | package-audit --project PATH | api-check --project PATH | release-verify --project PATH
        logs summarize --file PATH | sarif summarize --file PATH [--baseline PATH]
        artifact inspect --file PATH | verify --file PATH --sha256 HEX
        test-results summarize --file PATH | coverage summarize --file PATH
        jev noul|choice|score --input PATH [--dry-run] [--safe-input]
        jev screen --input PATH [--dry-run] [--safe-input] | cache-clear
        upstream status | update [--dry-run] | dotnet-skills <status|diff|check> [--dry-run]
        validate | eval [--skill NAME] [--results PATH] | release --output ZIP
        results init | new <audit|handoff|review|report> <name>
        results list [audit|handoff|review|report] [--json] | latest <type> [--json]
        results context <type> [--json] | clean [--dry-run]
        run status <UUID> | explain <UUID> | resume <UUID> | cancel <UUID> | list | abandon <UUID>

        Common: --root DIR (target repository), --toolkit DIR, --set NAME=VALUE (configuration override; repeatable), --json (schema-versioned output), --help
        JEV input: {"capability":"configured-id","purpose":"allowed-purpose","deterministicNarrowed":true,"state":"sanitized excerpt","instructions":"bounded question","criteria":...}
        Screen input: same routing metadata plus {"query":"question","candidates":[{"id":"path","text":"safe excerpt"}]}
        JEV defaults to auto; missing/invalid/uncertain answers return REVIEW for Codex.
        No command merges PRs, force-pushes, rewrites history, cleans unrelated paths, or installs external tools. git push-owned normally pushes one named local branch; git commit-owned creates a local commit from an exact validated staged path set.
        """;

    public static async Task<int> Main(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        var command = "unknown";
        try
        {
            var parsed = Cli.Parse(args);
            var toolkit = FindToolkit(parsed.Get("toolkit"));
            var builder = Host.CreateApplicationBuilder(args);
            Settings.AddConfigurationSources(builder.Configuration, toolkit);
            foreach (var (name, value) in parsed.ConfigurationOverrides()) builder.Configuration[name] = value;
            Settings.RegisterOptions(builder.Services, builder.Configuration, toolkit);
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            AgentToolModule.Register(builder.Services);
            using var host = builder.Build();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            await host.StartAsync(cancellation.Token);
            try
            {
                var c = Cli.Parse(args);
                command = c.Command;
                if (c.Flag("version") || command == "version")
                {
                    var version = Result.Ok(new { kind = "cli-version", schemaVersion = 1, product = Product, version = CliVersion, resultSchemaVersion = ResultSchemaVersion });
                    Console.WriteLine(json ? RenderJson(version, "version", Environment.CurrentDirectory, new()) : $"{Product} {CliVersion}");
                    return 0;
                }
                if (c.Flag("help") || c.Words.Count == 0 || command == "help")
                {
                    Console.WriteLine(json ? RenderJson(Result.Ok(new { help = Help }), "help", Environment.CurrentDirectory, new()) : Help);
                    return 0;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var root = Path.GetFullPath(c.Get("root") ?? Environment.CurrentDirectory);
                var settings = Settings.LoadFor(toolkit, command, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentTool.RuntimeSettingsOptions>>().Value.Settings);
                var result = await host.Services.GetRequiredService<AgentToolRuntime>().Execute(c, toolkit, root, settings, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Console.WriteLine(json ? RenderJson(result, command, root, settings.Output) : Render(result, root, settings.Output));
                return result.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                await host.StopAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or JsonException or FormatException or System.Xml.XmlException or System.ComponentModel.Win32Exception)
        {
            var result = new Result("error", null, 2);
            Console.Error.WriteLine(json
                ? SerializeEnvelope(result, command, new { code = "invalid-invocation", message = Secrets.Redact(e.Message) })
                : $"sdeveng: {Secrets.Redact(e.Message)}");
            return 2;
        }
        catch (Exception e)
        {
            var result = new Result("error", null, 70);
            Console.Error.WriteLine(json
                ? SerializeEnvelope(result, command, new { code = "internal-error", message = Secrets.Redact(e.Message) })
                : $"sdeveng: internal error: {Secrets.Redact(e.Message)}");
            return 70;
        }
    }

    public sealed class AgentToolRuntime
    {
        private readonly ILogger<AgentToolRuntime> _logger;
        private readonly IEnumerable<ICommandModule> _commandModules;
        public AgentToolRuntime(ILogger<AgentToolRuntime> logger, IEnumerable<ICommandModule> commandModules)
        {
            _logger = logger;
            _commandModules = commandModules;
        }
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Executing {Command} for {Root}", command.Command, root);
            var module = _commandModules.FirstOrDefault(candidate => candidate.CanHandle(command))
                ?? throw new ArgumentException("Unknown command. Use --help.");
            return module.Execute(command, toolkit, root, settings, cancellationToken);
        }
    }

    public interface ICommandModule
    {
        bool CanHandle(Cli command);
        Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken);
    }

    public static class AgentToolModule
    {
        public static IServiceCollection Register(IServiceCollection services)
        {
            services.AddSingleton<AgentToolRuntime>();
            services.AddSingleton(_ =>
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("sdeveng");
                return client;
            });
            services.AddTransient<GitHubReadClient>();
            services.AddTransient<IGitHubReadClient>(provider => provider.GetRequiredService<GitHubReadClient>());
            services.AddSingleton<IGitHubCredentialProvider, GitHubCredentialProvider>();
            services.AddTransient<GitHubWriteClient>();
            services.AddTransient<IGitHubWriteClient>(provider => provider.GetRequiredService<GitHubWriteClient>());
            services.AddTransient<GitHubIssueLabelWriter>();
            services.AddTransient<GitHubCommitReader>();
            services.AddTransient<GitHubRepositoryMetadataReader>();
            services.AddTransient<GitHubIssueReader>();
            services.AddTransient<StartWorkCoordinator>();
            services.AddTransient<GitHubChecksWorkflowReader>();
            services.AddTransient<GitHubActionsReader>();
            services.AddTransient<GitHubPrStatusReader>();
            services.AddTransient<GitHubReviewCommentReader>();
            services.AddSingleton<ITriageSemanticClassifier, AbstainingTriageSemanticClassifier>();
            services.AddSingleton<IGitHubAuthorizationProcess, GitHubAuthorizationProcess>();
            services.AddTransient<GitHubAuthorizationProbe>();
            services.AddSingleton<ICommandModule, InstallerCommandModule>();
            services.AddSingleton<ICommandModule, ConfigurationCommandModule>();
            services.AddSingleton<ICommandModule, DoctorCommandModule>();
            services.AddSingleton<ICommandModule, GitCommandModule>();
            services.AddSingleton<ICommandModule, RepoCommandModule>();
            services.AddSingleton<ICommandModule, GitHubCommandModule>();
            services.AddSingleton<ICommandModule, DotnetCommandModule>();
            services.AddSingleton<ICommandModule, ReportCommandModule>();
            services.AddSingleton<ICommandModule, JevCommandModule>();
            services.AddSingleton<ICommandModule, UpstreamCommandModule>();
            services.AddSingleton<ICommandModule, ValidateCommandModule>();
            services.AddSingleton<ICommandModule, EvalCommandModule>();
            services.AddSingleton<ICommandModule, ReleaseCommandModule>();
            services.AddSingleton<ICommandModule, RunCommandModule>();
            services.AddSingleton<ICommandModule, ResultsCommandModule>();
            return services;
        }
    }

    public sealed class ConfigurationCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "config explain";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("config explain");
            return Task.FromResult(Result.Ok(new
            {
                kind = "effective-config",
                schemaVersion = 1,
                settings,
                precedence = new[] { "toolkit JSON defaults", "environment variables", "invocation --set overrides" },
                sources = new
                {
                    toolkit = new[] { "config/jev.json", "config/output-limits.json", "config/repo-health.json", "config/toolkit.json" },
                    environmentVariables = new[] { "JEV_MODE", "TYPESAFE_API_URL", "JEV_MODEL", "JEV_TIMEOUT_SECONDS" },
                    invocationOverrides = "--set NAME=VALUE; supported for the listed environment variables",
                    machineAndUserFiles = "No separate machine- or user-level configuration files are loaded."
                }
            }));
        }
    }

    public sealed class ValidateCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "validate";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("validate");
            return Task.FromResult(Validation.Run(toolkit));
        }
    }

    public sealed class EvalCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "eval";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("eval");
            return Task.FromResult(Evaluation.Run(toolkit, command.Get("skill"), command.Get("results")));
        }
    }

    public sealed class ReleaseCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "release";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("release");
            return Task.FromResult(Release(toolkit, command.Require("output")));
        }
    }

    public sealed class ResultsCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        ["results init", "results new", "results list", "results latest", "results context", "results clean"];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var words = command.Words.Skip(2).ToArray();
            return name switch
            {
                "results init" => Init(words, root),
                "results new" => await Results.New(root, words),
                "results list" => Results.List(root, words),
                "results latest" => Results.Latest(root, words),
                "results context" => Results.Context(root, words),
                "results clean" => Clean(words, root, command.Flag("dry-run")),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }

        private static Result Init(string[] words, string root)
        {
            Results.RequireWords(words, 0, "Usage: results init.");
            return Results.Init(root);
        }

        private static Result Clean(string[] words, string root, bool dryRun)
        {
            Results.RequireWords(words, 0, "Usage: results clean [--dry-run].");
            return Results.Clean(root, dryRun);
        }
    }

    public sealed class RunCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "run status" or "run explain" or "run resume" or "run cancel" or "run list" or "run abandon";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var words = command.Words.Skip(2).ToArray();
            var directory = Path.Combine(root, ".sdeveng", "runs");
            if (command.Command == "run list")
            {
                if (words.Length != 0) throw new ArgumentException("Usage: run list.");
                return Task.FromResult(Result.Ok(LocalRunEventStore.List(directory)));
            }
            if (words.Length != 1 || !Guid.TryParseExact(words[0], "D", out var runId) || runId == Guid.Empty)
                throw new ArgumentException($"Usage: {command.Command} <UUID>.");
            var result = command.Command switch
            {
                "run status" => LocalRunEventStore.Status(directory, runId),
                "run explain" => LocalRunEventStore.Explain(directory, runId),
                "run resume" => LocalRunEventStore.Resume(directory, runId),
                "run abandon" => LocalRunEventStore.Abandon(directory, runId),
                _ => LocalRunEventStore.Cancel(directory, runId)
            };
            return Task.FromResult(Result.Ok(result));
        }
    }

    public sealed class InstallerCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "install" or "update" or "uninstall";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var home = command.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codexHome = command.Get("codex-home") ?? (command.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null);
            return Task.FromResult(Installer.Run(toolkit, home, codexHome, name, command.Flag("dry-run"), command.Flag("bin")));
        }
    }

    public sealed class DoctorCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "doctor";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("doctor");
            return Doctor(toolkit, settings, command);
        }

        private static async Task<Result> Doctor(string toolkit, Settings settings, Cli c)
        {
            var checks = new List<object>(); bool requiredOk = true;
            foreach (var (tool, args, required) in new[] { ("dotnet", new[] { "--version" }, true), ("git", new[] { "--version" }, true), ("gh", new[] { "--version" }, false), ("codex", new[] { "--version" }, false) })
            {
                try
                {
                    var r = await Processes.Run(tool, args, toolkit);
                    var ok = r.ExitCode == 0 && (tool != "dotnet" || Version.TryParse(r.Output.Trim().Split('-')[0], out var v) && v.Major >= 10);
                    if (tool == "dotnet")
                    {
                        ProcessResult runtimeResult;
                        try { runtimeResult = await Processes.Run("dotnet", ["--list-runtimes"], toolkit); }
                        catch (System.ComponentModel.Win32Exception) { runtimeResult = new(-1, ""); }
                        var diagnostics = DotnetDoctorDiagnostics.Evaluate(r.ExitCode, r.Output, runtimeResult.ExitCode, runtimeResult.Output);
                        checks.Add(new { tool, required, available = diagnostics.SdkAvailable, summary = Output.Compact(r.Output, settings.Output), sdkAvailable = diagnostics.SdkAvailable, sdkVersion = diagnostics.SdkVersion, runtimeAvailable = diagnostics.RuntimeAvailable, runtimes = diagnostics.Runtimes });
                        if (!diagnostics.SdkAvailable || !diagnostics.RuntimeAvailable) requiredOk = false;
                        continue;
                    }
                    if (tool == "gh")
                    {
                        ProcessResult authResult;
                        try { authResult = await Processes.Run("gh", ["auth", "status"], toolkit); }
                        catch (System.ComponentModel.Win32Exception) { authResult = new(-1, ""); }
                        var diagnostics = GitHubDoctorDiagnostics.Evaluate(ok, authResult.ExitCode);
                        checks.Add(new { tool, required, available = diagnostics.Available, authenticated = diagnostics.Authenticated, summary = Output.Compact(r.Output, settings.Output) });
                        continue;
                    }
                    checks.Add(new { tool, required, available = ok, summary = Output.Compact(r.Output, settings.Output) });
                    if (required && !ok) requiredOk = false;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    checks.Add(tool == "dotnet"
                        ? new { tool, required, available = false, sdkAvailable = false, sdkVersion = (string?)null, runtimeAvailable = false, runtimes = Array.Empty<string>() }
                        : new { tool, required, available = false });
                    if (required) requiredOk = false;
                }
            }
            var home = c.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codex = c.Get("codex-home") ?? (c.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null) ?? Path.Combine(home, ".codex");
            var classification = DoctorPrerequisiteDiagnostics.Classify(checks, requiredOk);
            var optionalTools = settings.Toolkit.OptionalTools.Select(t => new { name = t, available = Processes.OnPath(t) }).ToArray();
            return new(requiredOk ? "ok" : "failed", new { checks, prerequisites = new { required = new { status = classification.RequiredStatus, checks = classification.Required }, optional = new { status = classification.OptionalStatus, checks = classification.Optional, tools = optionalTools } }, toolkit, codex, skills = Path.Combine(home, ".agents/skills"), installation = Installer.Inspect(codex), jev = new { settings.Jev.Mode, credentials = JevCredentials.Status(), settings.Jev.Model }, upstream = "Run upstream status for integration policy; listed integrations are not automatically installed.", optionalTools }, requiredOk ? 0 : 1);
        }
    }

    internal static class DoctorPrerequisiteDiagnostics
    {
        public sealed record Classification(string RequiredStatus, object[] Required, string OptionalStatus, object[] Optional);

        public static Classification Classify(IEnumerable<object> checks, bool requiredOk)
        {
            var required = new List<object>();
            var optional = new List<object>();
            foreach (var check in checks)
            {
                var isRequired = (bool)check.GetType().GetProperty("required")!.GetValue(check)!;
                (isRequired ? required : optional).Add(check);
            }
            return new(requiredOk ? "ok" : "failed", required.ToArray(), "informational", optional.ToArray());
        }
    }

    internal static class GitHubDoctorDiagnostics
    {
        public sealed record State(bool Available, bool? Authenticated);

        public static State Evaluate(bool available, int authExitCode) =>
            new(available, available ? authExitCode == 0 : null);
    }

    internal static class DotnetDoctorDiagnostics
    {
        public sealed record State(bool SdkAvailable, string? SdkVersion, bool RuntimeAvailable, string[] Runtimes);

        public static State Evaluate(int sdkExitCode, string sdkOutput, int runtimeExitCode, string runtimeOutput)
        {
            var sdkVersion = sdkOutput.Trim();
            var sdkAvailable = sdkExitCode == 0 && Version.TryParse(sdkVersion.Split('-')[0], out var sdk) && sdk.Major >= 10;
            var runtimes = runtimeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal)
                    && Version.TryParse(line["Microsoft.NETCore.App ".Length..].Split(' ')[0], out var version) && version.Major == 10)
                .ToArray();
            return new(sdkAvailable, sdkAvailable ? sdkVersion : null, runtimeExitCode == 0 && runtimes.Length > 0, runtimes);
        }
    }

    public sealed class GitCommandModule : ICommandModule
    {
        private readonly GitHubIssueReader? _issueReader;

        public GitCommandModule(GitHubIssueReader? issueReader = null) => _issueReader = issueReader;

        public bool CanHandle(Cli command) => command.Command is "git state" or "git summary" or "git conflict-forecast" or "git prepare-commit" or "git stage-owned" or "git commit-owned" or "git issue-start" or "git branch-create" or "git worktree-create" or "git push-owned" or "git worktree-remove-owned" or "git stale-base" or "git abandon-owned";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            switch (command.Command)
            {
                case "git state": return Result.Ok(await Git.State(root));
                case "git summary": return Result.Ok(await Repository.Summary(root, command.Get("base"), settings.Output));
                case "git stale-base":
                    var expected = command.Require("expected");
                    if (!Regex.IsMatch(expected, "\\A[0-9a-fA-F]{40,64}\\z")) throw new ArgumentException("Expected base must be a full commit ID.");
                    var baseRef = command.Require("base");
                    var actual = (await Git.Require(root, "rev-parse", "--verify", baseRef + "^{commit}")).Trim();
                    return Result.Ok(new { baseRef, expected, actual, stale = !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) });
                case "git conflict-forecast": return Result.Ok(await Git.ConflictForecast(root, command.Require("base"), settings.Output));
                case "git prepare-commit":
                    await Git.EnsureSafe(root, false);
                    var diff = await Processes.Run("git", ["diff", "--check"], root);
                    var staged = await Processes.Run("git", ["diff", "--cached", "--check"], root);
                    return new(diff.ExitCode != 0 || staged.ExitCode != 0 ? "failed" : "ok", new { state = await Git.State(root), whitespace = Output.Compact(diff.Output + staged.Output, settings.Output), next = "Review explicit file scope before staging. Commit/push/PR creation remains caller-controlled; never merge without approval." }, diff.ExitCode != 0 || staged.ExitCode != 0 ? 1 : 0);
                case "git stage-owned": return Result.Ok(await Git.StageOwned(root, command.Require("paths-file")));
                case "git commit-owned": return Result.Ok(await Git.CommitOwned(root, command.Require("paths-file"), command.Require("message")));
                case "git issue-start":
                    await Git.EnsureSafe(root, true);
                    var issue = command.PositiveInt("issue"); var branch = command.Require("branch");
                    await Git.Require(root, "check-ref-format", "--branch", branch);
                    var reader = _issueReader ?? throw new InvalidOperationException("GitHub issue reader is unavailable; no branch created.");
                    var originUrl = (await Git.Require(root, "remote", "get-url", "origin")).Trim();
                    var (owner, repository) = GitHubRepositoryTarget(originUrl);
                    GitHubIssue issueInfo;
                    try { issueInfo = await reader.ReadIssueAsync(owner, repository, issue, cancellationToken); }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
                    { throw new InvalidOperationException("Issue is unavailable or not open; no branch created."); }
                    if (!string.Equals(issueInfo.State, "open", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Issue is unavailable or not open; no branch created.");
                    await AgentTool.Execute(Cli.Parse(["git", "branch-create", "--branch", branch]), toolkit, root, settings);
                    return Result.Ok(new { branch, issue });
                case "git branch-create":
                    await Git.EnsureSafe(root, true);
                    var newBranch = command.Require("branch");
                    await Git.Require(root, "check-ref-format", "--branch", newBranch);
                    await Git.Require(root, "switch", "-c", newBranch);
                    return Result.Ok(new { branch = newBranch });
                case "git worktree-create":
                    await Git.EnsureSafe(root, true);
                    var worktreeBranch = command.Require("branch");
                    var worktreePath = Path.GetFullPath(command.Require("path"));
                    await Git.Require(root, "check-ref-format", "--branch", worktreeBranch);
                    if (Directory.Exists(worktreePath) || File.Exists(worktreePath)) throw new InvalidOperationException("Worktree path already exists; no worktree created.");
                    await Git.Require(root, "worktree", "add", "-b", worktreeBranch, worktreePath);
                    return Result.Ok(new { branch = worktreeBranch, path = worktreePath });
                case "git push-owned":
                    var remote = command.Require("remote");
                    var pushBranch = command.Require("branch");
                    await Git.Require(root, "check-ref-format", "--branch", pushBranch);
                    var localRef = await Processes.Run("git", ["show-ref", "--verify", "--quiet", "refs/heads/" + pushBranch], root);
                    if (localRef.ExitCode != 0) throw new InvalidOperationException("Named local branch does not exist; no push performed.");
                    await Git.Require(root, "remote", "get-url", remote);
                    var pushState = await Git.State(root);
                    if (pushState.Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no push performed.");
                    var attemptedHead = pushState.Head;
                    var push = await Processes.Run("git", ["push", "--", remote, "refs/heads/" + pushBranch + ":refs/heads/" + pushBranch], root);
                    if (push.ExitCode != 0) return new("failed", new { remote, branch = pushBranch, head = attemptedHead, recovery = "Push failed; the local branch remains available. Inspect the remote and retry the named branch after resolving the failure.", evidence = Output.Compact(Secrets.Redact(push.Output), settings.Output) }, 1);
                    return Result.Ok(new { remote, branch = pushBranch, head = attemptedHead });
                case "git worktree-remove-owned":
                    var removePath = Path.GetFullPath(command.Require("path"));
                    var repoRoot = Path.GetFullPath((await Git.Require(root, "rev-parse", "--show-toplevel")).Trim());
                    if (string.Equals(removePath, repoRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Primary repository worktree cannot be removed.");
                    var worktrees = (await Git.Require(root, "worktree", "list", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var registered = worktrees.Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select(line => Path.GetFullPath(line[9..])).Any(path => string.Equals(path, removePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                    if (!registered || !Directory.Exists(removePath)) throw new InvalidOperationException("Path is not a registered linked worktree; nothing removed.");
                    var removeState = await Git.State(removePath);
                    if (!string.Equals(removeState.Root, removePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Path does not resolve to the named worktree; nothing removed.");
                    if (!removeState.Clean) throw new InvalidOperationException("Linked worktree is dirty; nothing removed.");
                    if (removeState.Operations.Count != 0) throw new InvalidOperationException("Linked worktree has an unfinished Git operation; nothing removed.");
                    await Git.Require(root, "worktree", "remove", removePath);
                    return Result.Ok(new { path = removePath });
                case "git abandon-owned":
                    var abandonBranch = command.Require("branch");
                    var abandonPath = Path.GetFullPath(command.Require("path"));
                    await Git.Require(root, "check-ref-format", "--branch", abandonBranch);
                    var primary = Path.GetFullPath((await Git.Require(root, "rev-parse", "--show-toplevel")).Trim());
                    if (string.Equals(primary, abandonPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Primary worktree cannot be abandoned.");
                    var listed = (await Git.Require(root, "worktree", "list", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (!listed.Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select(line => Path.GetFullPath(line[9..].TrimEnd('\r'))).Any(path => string.Equals(path, abandonPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) || !Directory.Exists(abandonPath)) throw new InvalidOperationException("Named linked worktree is unavailable.");
                    var state = await Git.State(abandonPath);
                    if (!string.Equals(state.Root, abandonPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || state.Branch != abandonBranch || !state.Clean || state.Operations.Count != 0) throw new InvalidOperationException("Named worktree is dirty, busy, or on another branch.");
                    var markerPath = Path.Combine((await Git.Require(abandonPath, "rev-parse", "--absolute-git-dir")).Trim(), GitOwnershipMarkers.WorktreeFileName);
                    if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != GitOwnershipMarkers.WorktreeFileContents ||
                        (await Git.Require(root, "config", "--get", "branch." + abandonBranch + "." + GitOwnershipMarkers.BranchConfigKey)).Trim() != GitOwnershipMarkers.BranchConfigValue)
                        throw new InvalidOperationException("Worktree and branch ownership markers are required.");
                    if ((await Processes.Run("git", ["merge-base", "--is-ancestor", "refs/heads/" + abandonBranch, "HEAD"], root)).ExitCode != 0)
                        throw new InvalidOperationException("Owned branch contains commits not merged into the primary HEAD; nothing removed.");
                    await Git.Require(root, "worktree", "remove", abandonPath);
                    await Git.Require(root, "branch", "-d", abandonBranch);
                    return Result.Ok(new { branch = abandonBranch, path = abandonPath, removed = true });
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }

        private static (string Owner, string Repository) GitHubRepositoryTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
                path = uri.AbsolutePath;
            }
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
            var parts = path.Trim('/').TrimEnd('/').Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
    }

    public sealed class RepoCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "repo changed-files" or "repo summary" or "repo locate" or "repo affected-projects" or "repo ownership" or "repo health" or "repo hygiene";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            switch (command.Command)
            {
                case "repo changed-files": return Result.Ok(await Git.Changed(root, command.Get("base")));
                case "repo summary": return Result.Ok(await Repository.Summary(root, command.Get("base"), settings.Output));
                case "repo locate":
                    var files = (await Git.Files(root)).Where(x => !SafeFiles.IsDiscoveryExcluded(x)).ToArray();
                    return Result.Ok(new { matches = files.Where(x => x.Contains(command.Require("query"), StringComparison.OrdinalIgnoreCase)).Take(settings.Output.MaxItems), total = files.Count(x => x.Contains(command.Require("query"), StringComparison.OrdinalIgnoreCase)), scope = "Git tracked + untracked, nonignored path names excluding the managed result store; use rg for symbols." });
                case "repo affected-projects": return Result.Ok(await Projects.Affected(root, await Git.Changed(root, command.Get("base"))));
                case "repo ownership": return Result.Ok(await Projects.Ownership(root, command.Require("file")));
                case "repo health":
                    var health = await Projects.Health(root, settings.Health);
                    return new(health.Count == 0 ? "ok" : "findings", health, health.Count == 0 ? 0 : 1);
                case "repo hygiene": return Result.Ok(await Repository.Hygiene(root, settings.Output));
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }
    }

    public sealed record GitHubCapability(string Operation, string? TargetRepository, string State, string ProbeKind, string Evidence);
    public sealed record GitHubCapabilities(string Kind, IReadOnlyList<GitHubCapability> Capabilities);

    public interface IGitHubAuthorizationProcess
    {
        Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd);
    }

    public sealed class GitHubAuthorizationProcess : IGitHubAuthorizationProcess
    {
        public Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, string cwd) => Processes.Run(executable, arguments, cwd);
    }

    /// <summary>Performs bounded authenticated, read-only GitHub authorization probes for the origin repository.</summary>
    public sealed class GitHubAuthorizationProbe(IGitHubAuthorizationProcess process)
    {
        public async Task<GitHubCapabilities> ProbeAsync(string root, CancellationToken cancellationToken = default)
        {
            ProcessResult remotes;
            try { remotes = await process.Run("git", ["remote", "get-url", "--all", "origin"], root); }
            catch { return Unknown(null, "origin could not be read"); }
            var urls = remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (remotes.ExitCode != 0 || urls.Length != 1) return Unknown(null, urls.Length == 0 ? "origin is missing" : "origin is ambiguous");
            string target;
            try { var parsed = ParseGitHubTarget(urls[0]); target = $"{parsed.Owner}/{parsed.Repository}"; }
            catch { return Unknown(null, "origin is not a canonical GitHub repository"); }
            ProcessResult auth;
            try { auth = await process.Run("gh", ["auth", "status"], root); }
            catch { return Unknown(target, "GitHub CLI authentication status unavailable"); }
            var authEvidence = auth.ExitCode == 0 ? auth.Output : "GitHub CLI authentication unavailable";
            ProcessResult issue;
            try { issue = await process.Run("gh", ["api", "--method", "GET", $"repos/{target}/issues?state=all&per_page=1"], root); }
            catch { issue = new(-1, ""); }
            var read = issue.ExitCode == 0 ? Cap("issues.read", target, "allowed", "authenticated repository issues GET succeeded")
                : ExplicitReject(issue.Output) ? Cap("issues.read", target, "denied", "GitHub explicitly rejected the authenticated issues GET")
                : Cap("issues.read", target, "unknown", "issues GET did not establish access");
            ProcessResult repo;
            try { repo = await process.Run("gh", ["api", "--method", "GET", $"repos/{target}"], root); }
            catch { repo = new(-1, ""); }
            var label = LabelCapability(target, repo, auth.ExitCode == 0 ? authEvidence : "");
            var credentialEvidence = auth.ExitCode == 0 ? authEvidence : "";
            var prCreate = PullRequestCreateCapability(target, repo, credentialEvidence);
            var branchPush = await BranchPushCapability(root, target, cancellationToken);
            var workflowRerun = await WorkflowRerunCapability(root, target);
            return new("github-capabilities", [read, label, branchPush, prCreate, workflowRerun]);
        }

        async Task<GitHubCapability> WorkflowRerunCapability(string root, string target)
        {
            ProcessResult actions;
            try { actions = await process.Run("gh", ["api", "--method", "GET", $"repos/{target}/actions/runs?per_page=1"], root); }
            catch { return Cap("workflow-rerun", target, "unknown", "actions-runs-get", "Actions read evidence unavailable; rerun write permission is unproven"); }
            if (ExplicitReject(actions.Output))
                return Cap("workflow-rerun", target, "denied", "actions-runs-get", "GitHub explicitly denied authenticated Actions access");
            return Cap("workflow-rerun", target, "unknown", "actions-runs-get", actions.ExitCode == 0
                ? "authenticated Actions GET succeeded; it does not prove rerun write permission"
                : "Actions GET did not establish access or rerun write permission");
        }

        async Task<GitHubCapability> BranchPushCapability(string root, string target, CancellationToken cancellationToken)
        {
            ProcessResult head;
            try { head = await process.Run("git", ["rev-parse", "--short=12", "HEAD"], root); }
            catch { return Cap("branch-push", target, "unknown", "current HEAD could not be identified"); }
            var shortHead = head.Output.Trim();
            if (head.ExitCode != 0 || !Regex.IsMatch(shortHead, @"^[0-9a-fA-F]{7,40}$"))
                return Cap("branch-push", target, "unknown", "current HEAD could not be identified");
            cancellationToken.ThrowIfCancellationRequested();
            var branch = $"refs/heads/roadmap/sdeveng-capability-probe-{shortHead}";
            ProcessResult push;
            try { push = await process.Run("git", ["push", "--dry-run", "--porcelain", "origin", $"HEAD:{branch}"], root); }
            catch { return Cap("branch-push", target, "unknown", "dry-run push did not establish authorization"); }
            if (push.ExitCode == 0) return Cap("branch-push", target, "allowed", "git-push-dry-run", "git push dry-run to a unique probe ref succeeded");
            return ExplicitReject(push.Output) || Regex.IsMatch(push.Output, @"(?i)(protected branch|pre-receive hook declined|prohibited by.*policy|repository rule|cannot push|push declined)")
                ? Cap("branch-push", target, "denied", "git-push-dry-run", "remote explicitly rejected the dry-run push")
                : Cap("branch-push", target, "unknown", "git-push-dry-run", "dry-run push failed without a deterministic authorization rejection");
        }

        static GitHubCapability PullRequestCreateCapability(string target, ProcessResult repository, string auth)
        {
            if (repository.ExitCode != 0)
                return ExplicitReject(repository.Output) ? Cap("pr-create", target, "denied", "GitHub explicitly rejected repository permission evidence") : Cap("pr-create", target, "unknown", "repository permission evidence unavailable");
            try
            {
                using var doc = JsonDocument.Parse(repository.Output);
                var permissions = doc.RootElement.GetProperty("permissions");
                var scopes = Regex.Match(auth, @"(?im)Token scopes:\s*(?<scopes>[^\r\n]+)");
                if (permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.False)
                    return Cap("pr-create", target, "denied", "repository permissions explicitly deny push access required by the same-repository pull request workflow");
                if (scopes.Success)
                {
                    var tokenScopes = scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries);
                    if (!tokenScopes.Contains("repo", StringComparer.Ordinal))
                        return Cap("pr-create", target, "denied", "authenticated token scopes explicitly lack the repository scope required for pull request creation");
                    if (permissions.TryGetProperty("push", out push) && push.ValueKind == JsonValueKind.True)
                        return Cap("pr-create", target, "allowed", "repository push permission and authenticated repo token scope are both explicit");
                }
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            return Cap("pr-create", target, "unknown", "available non-mutating evidence does not prove same-repository pull request creation permission");
        }

        static GitHubCapability LabelCapability(string target, ProcessResult repository, string auth)
        {
            if (repository.ExitCode != 0) return ExplicitReject(repository.Output) ? Cap("issues.labels.write", target, "denied", "GitHub explicitly rejected repository permission evidence") : Cap("issues.labels.write", target, "unknown", "repository permission evidence unavailable");
            try
            {
                using var doc = JsonDocument.Parse(repository.Output);
                var permissions = doc.RootElement.GetProperty("permissions");
                if (permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.False)
                    return Cap("issues.labels.write", target, "denied", "repository permissions explicitly deny push access required for issue label edits");
                var scopes = Regex.Match(auth, @"(?im)Token scopes:\s*(?<scopes>[^\r\n]+)");
                var hasRepoScope = scopes.Success && scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries).Contains("repo", StringComparer.Ordinal);
                var hasPublicRepoScope = scopes.Success && scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries).Contains("public_repo", StringComparer.Ordinal) &&
                    doc.RootElement.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.False;
                if (scopes.Success && permissions.TryGetProperty("push", out push) && push.ValueKind == JsonValueKind.True && (hasRepoScope || hasPublicRepoScope))
                    return Cap("issues.labels.write", target, "allowed", "repository push permission and a compatible GitHub token scope are both explicit");
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            return Cap("issues.labels.write", target, "unknown", "available non-mutating evidence does not prove both repository and credential write permission");
        }

        static bool ExplicitReject(string output) => Regex.IsMatch(output, @"(?i)(HTTP\s+401|HTTP\s+403|\b(unauthorized|forbidden|requires authentication|resource not accessible)\b)");
        static GitHubCapability Cap(string operation, string? target, string state, string evidence) => Cap(operation, target, state, "authenticated-get", evidence);
        static GitHubCapability Cap(string operation, string? target, string state, string probeKind, string evidence) => new(operation, target, state, probeKind, evidence);
        static GitHubCapabilities Unknown(string? target, string evidence) => new("github-capabilities", [Cap("issues.read", target, "unknown", evidence), Cap("issues.labels.write", target, "unknown", evidence), Cap("branch-push", target, "unknown", evidence), Cap("pr-create", target, "unknown", evidence), Cap("workflow-rerun", target, "unknown", evidence)]);
        public static (string Owner, string Repository) ParseGitHubTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0 && uri.Port == 443) path = uri.AbsolutePath;
            else if (Uri.TryCreate(remote, UriKind.Absolute, out uri) && uri.Scheme == "ssh" && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo == "git" && uri.Port == 22) path = uri.AbsolutePath;
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a canonical GitHub repository.");
            var parts = path.Trim('/').Split('/');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(parts[1], @"^[A-Za-z0-9_.-]+(?:\.git)?$")) throw new InvalidOperationException("Origin is not a canonical GitHub repository.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
    }

    public sealed class GitHubCommandModule : ICommandModule
    {
        private readonly GitHubPrStatusReader? _prStatusReader;
        private readonly GitHubReviewCommentReader? _reviewCommentReader;
        private readonly GitHubActionsReader? _actionsReader;
        private readonly GitHubAuthorizationProbe? _authorizationProbe;
        public GitHubCommandModule(GitHubPrStatusReader? prStatusReader = null, GitHubReviewCommentReader? reviewCommentReader = null, GitHubActionsReader? actionsReader = null, GitHubAuthorizationProbe? authorizationProbe = null, IGitHubLabelProcess? labelProcess = null)
        {
            _prStatusReader = prStatusReader;
            _reviewCommentReader = reviewCommentReader;
            _actionsReader = actionsReader;
            _authorizationProbe = authorizationProbe;
            _labelProcess = labelProcess ?? new GitHubLabelProcess();
        }
        private static (string Owner, string Repository) GitHubRepositoryTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Origin is not a GitHub repository.");
                path = uri.AbsolutePath;
            }
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a GitHub repository.");
            var parts = path.Trim('/').TrimEnd('/').Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) throw new InvalidOperationException("Origin is not a GitHub repository.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
        public bool CanHandle(Cli command) => command.Command is "github prepare-pr" or "github pr-status" or "github review-comments" or "github actions" or "github capabilities" or "github labels";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var artifacts = Path.Combine(root, ".agent-tool");
            switch (command.Command)
            {
                case "github capabilities":
                    var probe = _authorizationProbe ?? throw new InvalidOperationException("GitHub authorization probe is unavailable.");
                    return Result.Ok(await probe.ProbeAsync(root, cancellationToken));
                case "github labels":
                    var labelsOrigin = (await Git.Require(root, "remote", "get-url", "origin")).Trim();
                    var (labelsOwner, labelsRepository) = GitHubRepositoryTarget(labelsOrigin);
                    var catalogPath = Path.Combine(toolkit, "config", "labels.json");
                    using (var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, cancellationToken)))
                    {
                        var configured = catalog.RootElement.GetProperty("labels").EnumerateArray().Select(item => new
                        {
                            name = item.GetProperty("name").GetString() ?? throw new JsonException("Configured label name is missing."),
                            description = item.GetProperty("description").GetString() ?? "",
                            color = item.GetProperty("color").GetString() ?? throw new JsonException("Configured label color is missing.")
                        }).ToArray();
                        var api = _labelProcess ?? throw new InvalidOperationException("GitHub label process is unavailable.");
                        var target = $"{labelsOwner}/{labelsRepository}";
                        var listed = await api.Run("gh", ["api", "--paginate", "--slurp", $"repos/{target}/labels?per_page=100"], root);
                        if (listed.ExitCode != 0) throw new InvalidOperationException("Could not list remote GitHub labels.");
                        using var remoteDocument = JsonDocument.Parse(listed.Output);
                        if (remoteDocument.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub labels response must be an array.");
                        var pages = remoteDocument.RootElement.EnumerateArray().ToArray();
                        var remoteLabels = pages.Length > 0 && pages[0].ValueKind == JsonValueKind.Array
                            ? pages.SelectMany(page => page.EnumerateArray())
                            : pages.AsEnumerable();
                        var remoteNames = remoteLabels.Select(label => label.GetProperty("name").GetString() ?? throw new JsonException("Remote label name is missing.")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var missing = configured.Where(label => !remoteNames.Contains(label.name)).ToArray();
                        var configuredNames = configured.Select(label => label.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var unmanaged = remoteNames.Where(name => !configuredNames.Contains(name)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                        var created = new List<string>();
                        var applyLabels = command.Flag("apply");
                        if (applyLabels)
                            foreach (var label in missing)
                            {
                                var response = await api.Run("gh", ["api", "--method", "POST", $"repos/{target}/labels", "--field", $"name={label.name}", "--field", $"description={label.description}", "--field", $"color={label.color}"], root);
                                if (response.ExitCode != 0) throw new InvalidOperationException($"Could not create missing allowed label '{label.name}'.");
                                created.Add(label.name);
                            }
                        return Result.Ok(new { kind = "github-labels", repository = target, dryRun = !applyLabels, configured = configured.Select(x => x.name), remote = remoteNames.Order(StringComparer.OrdinalIgnoreCase), missing = applyLabels ? Array.Empty<string>() : missing.Select(x => x.name), unmanaged, created });
                    }
                case "github prepare-pr":
                    await Git.EnsureSafe(root, false);
                    var diff = await Processes.Run("git", ["diff", "--check"], root);
                    var staged = await Processes.Run("git", ["diff", "--cached", "--check"], root);
                    return new(diff.ExitCode != 0 || staged.ExitCode != 0 ? "failed" : "ok", new { state = await Git.State(root), whitespace = Output.Compact(diff.Output + staged.Output, settings.Output), next = "Review explicit file scope before staging. Commit/push/PR creation remains caller-controlled; never merge without approval." }, diff.ExitCode != 0 || staged.ExitCode != 0 ? 1 : 0);
                case "github pr-status":
                    var reader = _prStatusReader ?? throw new InvalidOperationException("GitHub pull request status reader is unavailable.");
                    var branch = (await Git.Require(root, "branch", "--show-current")).Trim();
                    if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Current checkout is detached; pull request status is unavailable.");
                    var origin = (await Git.Require(root, "remote", "get-url", "origin")).Trim();
                    var (owner, repository) = GitHubRepositoryTarget(origin);
                    return Result.Ok(await reader.ReadCurrentBranchAsync(owner, repository, branch, cancellationToken));
                case "github review-comments":
                    var pr = command.PositiveInt("pr").ToString(CultureInfo.InvariantCulture);
                    var reviewReader = _reviewCommentReader ?? throw new InvalidOperationException("GitHub review comment reader is unavailable.");
                    var originUrl = (await Git.Require(root, "remote", "get-url", "origin")).Trim();
                    var (reviewOwner, reviewRepository) = GitHubRepositoryTarget(originUrl);
                    var comments = new List<JsonElement>();
                    Uri? page = null;
                    do
                    {
                        var result = await reviewReader.ReadPageAsync(reviewOwner, reviewRepository, int.Parse(pr, CultureInfo.InvariantCulture), page, cancellationToken);
                        comments.AddRange(result.Comments.Select(comment => comment.Raw));
                        page = result.NextPage;
                    } while (page is not null);
                    SafeFiles.NoLinks(artifacts);
                    Directory.CreateDirectory(artifacts);
                    var path = Path.Combine(artifacts, $"github-review-comments-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(comments), cancellationToken);
                    return Result.Ok(new ProcessReport(0, Output.SummarizeFile(path, settings.Output), path));
                case "github actions":
                    var actionsReader = _actionsReader ?? throw new InvalidOperationException("GitHub Actions reader is unavailable.");
                    var actionsOrigin = (await Git.Require(root, "remote", "get-url", "origin")).Trim();
                    var (actionsOwner, actionsRepository) = GitHubRepositoryTarget(actionsOrigin);
                    return await GitHub.Actions(actionsReader, actionsOwner, actionsRepository, artifacts, command.Get("run-id"), command.Flag("failed-logs"), settings.Output, cancellationToken);
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }
        private readonly IGitHubLabelProcess? _labelProcess;
    }

    public sealed class DotnetCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        [
            "dotnet verify", "dotnet format", "dotnet package-audit", "dotnet dependencies",
            "dotnet api-check", "dotnet release-verify", "dotnet inspect", "dotnet build-plan",
            "dotnet test-plan", "dotnet diagnostics-plan"
        ];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var artifacts = Path.Combine(root, ".agent-tool");
            return name switch
            {
                "dotnet verify" or "dotnet format" or "dotnet package-audit" or "dotnet dependencies" or "dotnet api-check" or "dotnet release-verify" => await Dotnet(name, command, root, artifacts, settings),
                "dotnet inspect" => Result.Ok(await DotnetFacts.Inspect(root, command.Get("project"))),
                "dotnet build-plan" => Result.Ok(await DotnetFacts.BuildPlan(root, command.Get("project"), command.Get("base"), command.Get("configuration") ?? "Debug", command.Flag("binlog"))),
                "dotnet test-plan" => Result.Ok(await DotnetFacts.TestPlan(root, command.Get("project"), command.Get("base"), command.Get("configuration") ?? "Debug", new(command.Get("test"), command.Get("class"), command.Get("category"), command.Get("filter")))),
                "dotnet diagnostics-plan" => Result.Ok(await DotnetFacts.DiagnosticsPlan(command.Get("process-id"), command.Get("signal"), command.Get("duration-seconds"), root)),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }
    }

    public sealed class ReportCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        [
            "logs summarize", "sarif summarize", "artifact inspect", "artifact verify",
            "test-results summarize", "coverage summarize"
        ];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            return Task.FromResult(command.Command switch
            {
                "logs summarize" => Result.Ok(Output.SummarizeFile(command.Require("file"), settings.Output)),
                "sarif summarize" => Result.Ok(Output.Sarif(command.Require("file"), settings.Output, command.Get("baseline"))),
                "artifact inspect" => Result.Ok(Artifacts.Inspect(command.Require("file"), settings.Output)),
                "artifact verify" => Artifacts.Verify(command.Require("file"), command.Require("sha256")),
                "test-results summarize" => Result.Ok(DotnetArtifacts.TestResults(command.Require("file"), settings.Output)),
                "coverage summarize" => Result.Ok(DotnetArtifacts.Coverage(command.Require("file"), settings.Output)),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            });
        }
    }

    public sealed class JevCommandModule : ICommandModule
    {
        private static readonly string[] Commands = ["jev noul", "jev choice", "jev score", "jev screen", "jev cache-clear"];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            if (command.Command == "jev cache-clear")
            {
                var cachePath = Path.Combine(root, ".agent-tool", "jev-cache");
                SafeFiles.NoLinks(cachePath);
                if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
                return Result.Ok(new { cleared = cachePath });
            }

            return await JevCommand(command, command.Command[4..], root, settings.Jev);
        }
    }

    public sealed class UpstreamCommandModule : ICommandModule
    {
        readonly GitHubCommitReader? _commitReader;
        public UpstreamCommandModule(GitHubCommitReader? commitReader = null) => _commitReader = commitReader;
        public bool CanHandle(Cli command) => command.Command is "upstream status" or "upstream update" or "upstream dotnet-skills";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var artifacts = Path.Combine(root, ".agent-tool");
            return command.Command switch
            {
                "upstream status" => Result.Ok(new { plugins = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))), tools = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/tools.json"))), versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json"))) }),
                "upstream update" => await Upstream(toolkit, artifacts, command.Flag("dry-run"), _commitReader),
                "upstream dotnet-skills" => await DotnetSkillsDrift.Run(toolkit, artifacts, command.Words.Skip(2).SingleOrDefault(), command.Flag("dry-run"), _commitReader),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }
    }

    public sealed class RuntimeSettingsOptions { public Settings Settings { get; set; } = new(new(), new(), new(), new()); public string? ValidationError { get; set; } }

    public static string RenderJson(Result result, string command, string root, OutputSettings output)
    {
        var rendered = SerializeEnvelope(result, command, null);
        if (rendered.Length <= output.MaxOutputChars) return rendered;
        var report = Path.Combine(root, ".agent-tool", $"result-{Guid.NewGuid():N}.json");
        SafeFiles.Atomic(report, rendered);
        return SerializeEnvelope(new(result.Status, new { truncated = true, characters = rendered.Length, artifact = report }, result.ExitCode), command, null);
    }

    public static string SerializeEnvelope(Result result, string command, object? error) => Secrets.RedactJson(JsonSerializer.Serialize(new
    {
        schemaVersion = ResultSchemaVersion,
        product = Product,
        cliVersion = CliVersion,
        command,
        result.Status,
        result.ExitCode,
        data = result.Data,
        error
    }, Json));

    public static string Render(Result result, string root, OutputSettings output)
    {
        var rendered = Secrets.RedactJson(JsonSerializer.Serialize(result, Json));
        if (rendered.Length <= output.MaxOutputChars) return rendered;
        var report = Path.Combine(root, ".agent-tool", $"result-{Guid.NewGuid():N}.json");
        SafeFiles.Atomic(report, rendered);
        return Secrets.RedactJson(JsonSerializer.Serialize(new { result.Status, result.ExitCode, truncated = true, characters = rendered.Length, artifact = report }, Json));
    }

    public static string FindToolkit(string? explicitRoot = null, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var path = explicitRoot ?? Environment.GetEnvironmentVariable("SDEVENG_ROOT") ?? Environment.GetEnvironmentVariable("CODEX_TOOLKIT_ROOT");
        if (path is null)
        {
            if (File.Exists(source)) source = File.ResolveLinkTarget(source, true)?.FullName ?? source;
            for (var parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
            for (var parent = Environment.CurrentDirectory; path is null && parent is not null; parent = Directory.GetParent(parent)?.FullName)
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
        }
        path ??= Environment.CurrentDirectory;
        path = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(path, "config", "toolkit.json"))) throw new ArgumentException("Toolkit root not found; pass --toolkit DIR, SDEVENG_ROOT, or legacy CODEX_TOOLKIT_ROOT.");
        return path;
    }

    public static Task<Result> Execute(Cli c, string toolkit, string root, Settings settings)
    {
        ICommandModule[] modules =
        [
            new InstallerCommandModule(), new DoctorCommandModule(), new GitCommandModule(), new RepoCommandModule(),
            new GitHubCommandModule(), new DotnetCommandModule(), new ReportCommandModule(), new JevCommandModule(),
            new UpstreamCommandModule(), new ValidateCommandModule(), new EvalCommandModule(), new ReleaseCommandModule(), new RunCommandModule(),
            new ResultsCommandModule()
        ];
        return new AgentToolRuntime(NullLogger<AgentToolRuntime>.Instance, modules)
            .Execute(c, toolkit, root, settings);
    }

    static async Task<Result> Dotnet(string command, Cli c, string root, string artifacts, Settings settings)
    {
        var explicitProject = c.Get("project");
        if (command != "dotnet verify" && explicitProject is null) throw new ArgumentException("This command requires --project PATH (project or solution).");
        var targets = explicitProject is not null ? new[] { Path.GetFullPath(explicitProject, root) } : (await Projects.Affected(root, await Git.Changed(root, c.Get("base")))).Projects.ToArray();
        if (targets.Any(x => !File.Exists(x))) throw new ArgumentException("Project or solution does not exist.");
        var results = new List<Result>();
        foreach (var project in targets)
        {
            if (command == "dotnet format")
            {
                var changed = await Git.Changed(root, c.Get("base"));
                var code = changed.Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root, x))).Select(x => Path.Combine(root, x)).ToArray();
                if (code.Length == 0) continue;
                var args = new List<string> { "format", project, "--include" }; args.AddRange(code);
                if (!c.Flag("apply")) args.Add("--verify-no-changes");
                results.Add(await RunArtifact("dotnet", args, root, artifacts, settings.Output));
            }
            else if (command == "dotnet dependencies")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--include-transitive", "--format", "json", "--output-version", "1"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p) r = Result.Ok(DotnetArtifacts.Dependencies(p.Artifact, root, settings.Output));
                results.Add(r);
            }
            else if (command == "dotnet package-audit")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--vulnerable", "--include-transitive", "--format", "json"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p)
                {
                    var report = JsonNode.Parse(File.ReadAllText(p.Artifact));
                    var vulnerabilities = Audit.Count(report);
                    r = new(vulnerabilities > 0 ? "vulnerable" : "ok", new { vulnerabilities, p.Artifact }, vulnerabilities > 0 ? 1 : 0);
                }
                results.Add(r);
            }
            else if (command == "dotnet api-check")
            {
                if (!await Projects.HasApiChecks(root, project)) throw new InvalidOperationException("Configure PublicApiAnalyzers or EnablePackageValidation with a baseline first; api-check cannot certify an unconfigured project.");
                results.Add(await RunArtifact("dotnet", ["pack", project, "-p:EnablePackageValidation=true", "-p:TreatWarningsAsErrors=true"], root, artifacts, settings.Output));
            }
            else
            {
                var steps = new List<string[]> { new[] { "build", project, "--nologo" } };
                if (command == "dotnet release-verify") steps.Insert(0, ["restore", project, "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"]);
                if (command == "dotnet release-verify") steps.Add(["format", project, "--verify-no-changes"]);
                var isSolution = project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
                if (isSolution || await Projects.IsTest(root, project)) steps.Add(["test", project, "--no-build", "--nologo"]);
                else if (command == "dotnet release-verify")
                {
                    var tests = await Projects.DependentTests(root, project);
                    if (tests.Length == 0) throw new InvalidOperationException("Release verification cannot establish test coverage for this project. Pass a solution or add a dependent test project.");
                    steps.AddRange(tests.Select(test => new[] { "test", test, "--nologo" }));
                }
                foreach (var step in steps)
                {
                    var r = await RunArtifact("dotnet", step, root, artifacts, settings.Output); results.Add(r);
                    if (r.ExitCode != 0) break;
                }
                if (command == "dotnet release-verify" && results.All(r => r.ExitCode == 0))
                    results.Add(await Dotnet("dotnet package-audit", c, root, artifacts, settings));
            }
        }
        return new(results.Any(x => x.ExitCode != 0) ? "failed" : "ok", new { targets, results, scope = command == "dotnet release-verify" ? "restore, build, format, established test coverage, vulnerability audit; API/SBOM/reproducibility gates are separate on-demand skills" : "targeted" }, results.Any(x => x.ExitCode != 0) ? 1 : 0);
    }

    public static async Task<Result> RunArtifact(string executable, IEnumerable<string> args, string root, string artifacts, OutputSettings limits)
    {
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var path = Path.Combine(artifacts, $"{executable}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
        var r = await Processes.Run(executable, args, root, path, TimeSpan.FromMinutes(20));
        return new(r.ExitCode == 0 ? "ok" : "failed", new ProcessReport(r.ExitCode, Output.SummarizeFile(path, limits), path), r.ExitCode == 0 ? 0 : 1);
    }

    static async Task<Result> JevCommand(Cli c, string kind, string root, JevSettings settings)
    {
        var inputPath = c.Require("input");
        if (Path.GetFileName(inputPath).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return Result.Review("Sensitive input path refused.");
        if (new FileInfo(inputPath).Length > settings.MaxInputBytes) return Result.Review("Input exceeds configured limit.");
        var input = JsonNode.Parse(File.ReadAllText(inputPath)) as JsonObject;
        if (input is null)
        {
            if (kind == "screen") return Result.Review("Screen input must be an object; no candidate discarded.");
            throw new ArgumentException("Input object required.");
        }
        var capability = input["capability"] is JsonValue capabilityValue && capabilityValue.TryGetValue<string>(out var capabilityText) ? capabilityText : "";
        var purpose = input["purpose"] is JsonValue purposeValue && purposeValue.TryGetValue<string>(out var purposeText) ? purposeText : "";
        settings.Capabilities.TryGetValue(capability, out var policy);
        if (policy is null) return JevClient.PolicyReview(null, capability, purpose, "A configured JEV capability is required.");
        if (!policy.Purposes.Contains(purpose, StringComparer.Ordinal)) return JevClient.PolicyReview(policy, capability, purpose, "Purpose is not allowed for this JEV capability.");
        if (!policy.Allowed) return JevClient.PolicyReview(policy, capability, purpose, "JEV is disallowed for this capability; use deterministic tooling or GPT reasoning.");
        var deterministicallyNarrowed = input["deterministicNarrowed"] is JsonValue narrowedValue && narrowedValue.TryGetValue<bool>(out var narrowed) && narrowed;
        if (policy.DeterministicFirst && !deterministicallyNarrowed) return JevClient.PolicyReview(policy, capability, purpose, "Deterministic narrowing is required before JEV.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        var client = new JevClient(http, settings, Path.Combine(root, ".agent-tool/jev-cache"));
        if (kind == "screen")
        {
            if (input["candidates"] is not JsonArray candidates) return JevClient.PolicyReview(policy, capability, purpose, "Screen candidates array is required; no candidate discarded.");
            var candidateLimit = Math.Min(settings.MaxCandidates, Math.Min(policy.MaxCandidates, policy.MaxCalls));
            if (candidates.Count > candidateLimit) return JevClient.PolicyReview(policy, capability, purpose, "Too many candidates for the capability call budget; narrow deterministic search first.");
            var query = input["query"] is JsonValue queryValue && queryValue.TryGetValue<string>(out var queryText) ? queryText : null;
            if (string.IsNullOrWhiteSpace(query)) return JevClient.PolicyReview(policy, capability, purpose, "Screen query is required; no candidate discarded.");
            var prepared = new List<(string Id, string Text)>();
            foreach (var candidate in candidates)
            {
                if (candidate is not JsonObject item || item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || item["text"] is not JsonValue textValue || !textValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                    return JevClient.PolicyReview(policy, capability, purpose, "Every screen candidate requires a non-empty string id and text; no candidate discarded.");
                if (Secrets.LooksSensitive(id)) return JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected in candidate id; no candidate discarded.");
                prepared.Add((id, text));
            }
            if (prepared.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != prepared.Count)
                return JevClient.PolicyReview(policy, capability, purpose, "Screen candidate ids must be unique; no candidate discarded.");
            var answers = new List<object>(); int exitCode = 0, remoteCalls = 0, cacheHits = 0, fallbacks = 0, escalations = 0, uncertain = 0, contextAvoidedBytes = 0;
            foreach (var candidate in prepared)
            {
                var request = JevClient.Request("noul", candidate.Text, $"Is this candidate relevant to: {query}", null, settings.Model);
                var judgment = Secrets.LooksSensitive(request.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : c.Flag("dry-run") ? Result.Ok(request) : !c.Flag("safe-input") ? JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after minimizing and reviewing supplied text for external transmission.") : await client.Judge(request, policy, purpose, capability);
                exitCode = Math.Max(exitCode, judgment.ExitCode);
                answers.Add(new { id = candidate.Id, judgment });
                if (judgment.Status == "EXCLUDE") contextAvoidedBytes += Encoding.UTF8.GetByteCount(candidate.Text);
                if (judgment.Status == "REVIEW") uncertain++;
                var telemetry = JsonSerializer.SerializeToNode(judgment.Data, Json)?["instrumentation"];
                remoteCalls += telemetry?["counts"]?["remoteCalls"]?.GetValue<int>() ?? 0;
                cacheHits += telemetry?["counts"]?["cacheHits"]?.GetValue<int>() ?? 0;
                fallbacks += telemetry?["counts"]?["fallbacks"]?.GetValue<int>() ?? 0;
                escalations += telemetry?["counts"]?["escalations"]?.GetValue<int>() ?? 0;
            }
            return new(exitCode == 0 ? "ok" : "REVIEW", new
            {
                judgments = answers,
                instrumentation = new
                {
                    schemaVersion = 1,
                    capability,
                    purpose,
                    privacy = policy.Privacy,
                    budget = new { expectedCalls = policy.ExpectedCalls, maxCalls = policy.MaxCalls },
                    bounds = new { policy.DeterministicFirst, policy.MaxInputBytes, policy.MaxCandidates },
                    counts = new { candidates = prepared.Count, judgments = c.Flag("dry-run") ? 0 : prepared.Count, remoteCalls, cacheHits, fallbacks, escalations },
                    confidence = new { reported = (double?)null, minimum = policy.MinConfidence, uncertain },
                    fallback = new { used = fallbacks > 0, target = fallbacks > 0 ? "GPT" : null },
                    escalation = new { required = escalations > 0, target = escalations > 0 ? policy.GptEscalation + "-gpt" : null },
                    contextAvoidedBytes,
                    payloadCaptured = false
                }
            }, exitCode);
        }
        if (policy.MaxCalls < 1) return JevClient.PolicyReview(policy, capability, purpose, "JEV call budget is zero for this capability.");
        var payload = JevClient.Request(kind, input["state"]?.GetValue<string>() ?? "", input["instructions"]?.GetValue<string>() ?? "", input["criteria"], settings.Model);
        if (c.Flag("dry-run")) return Secrets.LooksSensitive(payload.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : Result.Ok(payload);
        if (!c.Flag("safe-input")) return JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after reviewing and minimizing supplied text for external transmission.");
        return await client.Judge(payload, policy, purpose, capability);
    }

    static async Task<Result> Upstream(string toolkit, string artifacts, bool dryRun, GitHubCommitReader? commitReader)
    {
        var versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json")))!["repositories"]!.AsArray();
        var rows = new List<object>();
        foreach (var entry in versions)
        {
            var repo = entry!["repository"]!.GetValue<string>(); var pinned = entry["revision"]?.GetValue<string>();
            if (dryRun) { rows.Add(new { repo, pinned, query = $"gh api repos/{repo}/commits/HEAD --jq .sha" }); continue; }
            try
            {
                var parts = repo.Split('/', 2);
                if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
                var latest = (await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).ReadHeadAsync(parts[0], parts[1])).Sha;
                rows.Add(new { repo, pinned, latest, status = latest == pinned ? "current" : "review-update" });
            }
            catch (GitHubTransportException) { rows.Add(new { repo, pinned, latest = (string?)null, status = "unavailable" }); }
        }
        if (dryRun) return Result.Ok(rows);
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "upstream-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(rows, Json));
        return Result.Ok(new { rows, report, policy = "Report only; no downloads, manifest edits or merges." });
    }

    static Result Release(string toolkit, string output)
    {
        var valid = Validation.Run(toolkit); if (valid.ExitCode != 0) return valid;
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var zip = System.IO.Compression.ZipFile.Open(output, System.IO.Compression.ZipArchiveMode.Create);
        var roots = new[] { "agents", "config", "docs", "evals", "global", "plugins", "schemas", "templates", "tools", "upstream" };
        foreach (var file in roots.SelectMany(x => SafeFiles.Enumerate(Path.Combine(toolkit, x))).Concat(new[] { "README.md", "CHANGELOG.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.md", "global.json", ".agents/plugins/marketplace.json" }.Select(x => Path.Combine(toolkit, x))))
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, Path.GetRelativePath(toolkit, file).Replace('\\', '/'));
        return Result.Ok(new { archive = output });
    }
}

public record Result(string Status, object? Data, int ExitCode = 0)
{
    public static Result Ok(object? data) => new("ok", data);
    public static Result Review(string reason) => new("REVIEW", new { reason, fallback = "Codex" });
}

public enum PromptReturnStatus { SUCCESS, WAITING_FOR_REVIEW, BLOCKED, OUT_OF_USAGE, FAILURE }

public static class PromptReturnStatusContract
{
    const string Marker = "PROMPT_RETURN_STATUS:";

    public static PromptReturnStatus Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var lines = response.Split('\n');
        var markers = lines.Select((line, index) => (line, index))
            .Where(item => item.line.Contains(Marker, StringComparison.Ordinal)).ToArray();
        if (markers.Length != 1) throw new FormatException("Response must contain exactly one terminal prompt return status marker.");
        var (markerLine, markerIndex) = markers[0];
        var trimmedLine = markerLine.TrimEnd('\r', ' ', '\t');
        var expectedPrefix = Marker + " ";
        if (markerIndex != Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line)) ||
            !trimmedLine.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new FormatException("Prompt return status marker must be the final non-whitespace content.");
        var value = trimmedLine[expectedPrefix.Length..];
        if (!Enum.TryParse<PromptReturnStatus>(value, ignoreCase: false, out var status) || !Enum.IsDefined(status) || value != status.ToString())
            throw new FormatException("Prompt return status marker has an invalid status.");
        return status;
    }
}

public record ProcessReport(int ProcessExitCode, object Summary, string Artifact);
public record ProcessResult(int ExitCode, string Output);

public static class DotnetSkillsDrift
{
    // This compares only public Git metadata. It deliberately never checks out, runs, or imports upstream files.
    public static async Task<Result> Run(string toolkit, string artifacts, string? operation, bool dryRun, GitHubCommitReader? commitReader = null)
    {
        if (operation is not ("status" or "diff" or "check")) throw new ArgumentException("Usage: upstream dotnet-skills <status|diff|check> [--dry-run].");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))) ?? throw new FormatException("Dotnet skills provenance manifest is empty.");
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Dotnet skills provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        if (operation == "status")
        {
            if (dryRun) return Result.Ok(new { kind = "dotnet-skills-status", repository, pinned, query = $"gh api repos/{repository}/commits/HEAD --jq .sha", policy = "Public metadata only; no source is downloaded or executed." });
            try
            {
                var parts = repository.Split('/', 2);
                if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
                var latest = (await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).ReadHeadAsync(parts[0], parts[1])).Sha;
                return Result.Ok(new { kind = "dotnet-skills-status", repository, pinned, latest, status = latest == pinned ? "current" : "review-update", policy = "Public metadata only; no source is downloaded or executed." });
            }
            catch (GitHubTransportException) { return new("unavailable", new { kind = "dotnet-skills-status", repository, pinned, latest = (string?)null, status = "review-update", policy = "Public metadata only; no source is downloaded or executed." }, 1); }
        }
        if (dryRun) return Result.Ok(new { kind = "dotnet-skills-diff", repository, pinned, query = $"gh api repos/{repository}/compare/{pinned}...HEAD", policy = "Only changed decision paths are reported; no upstream code is executed, merged, or copied." });
        JsonObject authoritative;
        try
        {
            var parts = repository.Split('/', 2);
            if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
            var compare = await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).CompareToHeadAsync(parts[0], parts[1], pinned);
            authoritative = new JsonObject { ["head_commit"] = new JsonObject { ["sha"] = compare.HeadCommitSha }, ["files"] = new JsonArray(compare.Files.Select(file => (JsonNode?)new JsonObject { ["filename"] = file.Filename, ["status"] = file.Status, ["previous_filename"] = file.PreviousFilename }).ToArray()) };
        }
        catch (GitHubTransportException) { return new("unavailable", new { kind = "dotnet-skills-diff", repository, pinned, policy = "Comparison unavailable; no source was downloaded or executed." }, 1); }
        var analysis = Analyze(manifest, authoritative);
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "dotnet-skills-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(analysis, AgentTool.Json));
        var review = analysis["classification"]?.GetValue<string>() is "relevant" or "review-required";
        return new(review && operation == "check" ? "review-required" : "ok", new { kind = "dotnet-skills-drift", analysis, report, policy = "Report only. Review listed public paths before any separate, explicit integration change." }, review && operation == "check" ? 1 : 0);
    }

    public static JsonObject Analyze(JsonNode manifest, JsonNode authoritative)
    {
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        var files = authoritative["files"]?.AsArray() ?? throw new FormatException("Authoritative comparison has no files array.");
        var paths = manifest["decisions"]?.AsArray().SelectMany(d => d?["upstreamPaths"]?.AsArray() ?? throw new FormatException("Decision has no upstreamPaths.")).Select(p => p?.GetValue<string>() ?? throw new FormatException("Decision path is invalid.")).ToHashSet(StringComparer.Ordinal) ?? throw new FormatException("Provenance decisions are missing.");
        var relevant = new JsonArray(); var irrelevant = 0;
        foreach (var item in files)
        {
            var file = item?.AsObject() ?? throw new FormatException("Authoritative comparison file is invalid.");
            var path = Required(file, "filename"); var status = Required(file, "status");
            if (status is not ("added" or "modified" or "removed" or "renamed")) throw new FormatException("Authoritative comparison has an unknown file status.");
            var previous = file["previous_filename"]?.GetValue<string>();
            if (!paths.Contains(path) && (previous is null || !paths.Contains(previous))) { irrelevant++; continue; }
            var disposition = status is "removed" or "renamed" ? "review-required" : "relevant";
            relevant.Add(new JsonObject { ["path"] = path, ["status"] = status, ["previousPath"] = previous, ["classification"] = disposition, ["inspect"] = "Fetch this one public path only if a maintainer needs its diff." });
        }
        var classification = relevant.Count == 0 ? (files.Count == 0 ? "no-change" : "irrelevant") : relevant.Any(f => f!["classification"]!.GetValue<string>() == "review-required") ? "review-required" : "relevant";
        return new JsonObject { ["schemaVersion"] = 1, ["kind"] = "dotnet-skills-drift", ["repository"] = repository, ["pinned"] = pinned, ["authoritativeHead"] = authoritative["head_commit"]?["sha"]?.GetValue<string>(), ["classification"] = classification, ["changedFiles"] = files.Count, ["relevant"] = relevant, ["irrelevantCount"] = irrelevant, ["automaticAction"] = "none" };
    }

    static string Required(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 } text ? text : throw new FormatException($"Missing {name}.");
}

public sealed class Cli
{
    public string Command => Words.FirstOrDefault() is "results" ? string.Join(' ', Words.Take(2))
        : Words.FirstOrDefault() == "upstream" && Words.ElementAtOrDefault(1) == "dotnet-skills" ? string.Join(' ', Words.Take(2))
        : Words.FirstOrDefault() == "run" ? string.Join(' ', Words.Take(2))
        : string.Join(' ', Words);

    public void ValidateCommand(string command)
    {
        var allowed = new HashSet<string>(new[] { "root", "toolkit", "set", "json", "help", "version" });
        string[] specific = command switch
        {
            "config explain" => ["set"],
            "install" or "update" => ["home", "codex-home", "dry-run", "bin"],
            "uninstall" => ["home", "codex-home", "dry-run"],
            "doctor" => ["home", "codex-home"],
            "repo changed-files" or "repo affected-projects" or "repo summary" or "git summary" or "git conflict-forecast" => ["base"],
            "repo locate" => ["query"],
            "repo ownership" => ["file"],
            "git issue-start" => ["issue", "branch"],
            "git stage-owned" => ["paths-file"],
            "git commit-owned" => ["paths-file", "message"],
            "git branch-create" => ["branch"],
            "git worktree-create" => ["branch", "path"],
            "git push-owned" => ["remote", "branch"],
            "git worktree-remove-owned" => ["path"],
            "git stale-base" => ["base", "expected"],
            "git abandon-owned" => ["branch", "path"],
            "github review-comments" => ["pr"],
            "github labels" => ["apply", "dry-run"],
            "github actions" => ["run-id", "failed-logs"],
            "dotnet verify" => ["base", "project"],
            "dotnet inspect" => ["project"],
            "dotnet build-plan" => ["base", "project", "configuration", "binlog"],
            "dotnet test-plan" => ["base", "project", "configuration", "test", "class", "category", "filter"],
            "dotnet diagnostics-plan" => ["process-id", "signal", "duration-seconds"],
            "dotnet format" => ["base", "project", "apply"],
            "dotnet dependencies" or "dotnet package-audit" or "dotnet api-check" or "dotnet release-verify" => ["project"],
            "logs summarize" or "test-results summarize" or "coverage summarize" or "artifact inspect" => ["file"],
            "sarif summarize" => ["file", "baseline"],
            "artifact verify" => ["file", "sha256"],
            "jev noul" or "jev choice" or "jev score" or "jev screen" => ["input", "dry-run", "safe-input"],
            "upstream update" or "upstream dotnet-skills" => ["dry-run"],
            "eval" => ["skill", "results"],
            "release" => ["output"],
            "results clean" => ["dry-run"],
            _ => []
        };
        allowed.UnionWith(specific);
        foreach (var option in Options.Keys)
            if (!allowed.Contains(option)) throw new ArgumentException($"--{option} is not supported by this command.");
        if (command == "github labels" && Flag("apply") && Flag("dry-run"))
            throw new ArgumentException("github labels accepts either --dry-run or --apply, not both.");
    }
    public List<string> Words { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);
    static readonly HashSet<string> Flags = ["json", "help", "version", "dry-run", "bin", "apply", "safe-input", "binlog", "failed-logs"];
    static readonly HashSet<string> Values = ["root", "toolkit", "set", "home", "codex-home", "base", "expected", "baseline", "query", "issue", "branch", "remote", "path", "paths-file", "message", "pr", "run-id", "project", "file", "sha256", "input", "output", "skill", "results", "configuration", "test", "class", "category", "filter", "process-id", "signal", "duration-seconds"];
    public string? Get(string name) => Options.GetValueOrDefault(name);
    public bool Flag(string name) => Options.ContainsKey(name);
    public string Require(string name) => Get(name) is { Length: > 0 } v ? v : throw new ArgumentException($"--{name} is required.");
    public IEnumerable<KeyValuePair<string, string?>> ConfigurationOverrides()
    {
        var allowed = new HashSet<string>(["JEV_MODE", "TYPESAFE_API_URL", "JEV_MODEL", "JEV_TIMEOUT_SECONDS"], StringComparer.Ordinal);
        foreach (var entry in (Options.GetValueOrDefault("set") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2 || !allowed.Contains(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
                throw new ArgumentException("--set requires NAME=VALUE for a supported setting: JEV_MODE, TYPESAFE_API_URL, JEV_MODEL, or JEV_TIMEOUT_SECONDS.");
            yield return new(pair[0], pair[1]);
        }
    }
    public int PositiveInt(string name) => int.TryParse(Require(name), out var i) && i > 0 ? i : throw new ArgumentException($"--{name} must be a positive integer.");
    public static Cli Parse(string[] args)
    {
        var result = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-h") arg = "--help";
            if (arg == "-V") arg = "--version";
            if (!arg.StartsWith('-')) { result.Words.Add(arg); continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Only long options are supported.");
            var parts = arg[2..].Split('=', 2); var name = parts[0];
            if (!Flags.Contains(name) && !Values.Contains(name)) throw new ArgumentException($"Unknown option --{name}.");
            if (result.Options.ContainsKey(name) && name != "set") throw new ArgumentException($"Duplicate --{name}.");
            if (Flags.Contains(name)) { if (parts.Length != 1) throw new ArgumentException($"--{name} takes no value."); result.Options[name] = null; }
            else
            {
                var value = parts.Length == 2 ? parts[1] : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException($"Missing value for --{name}.");
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Empty --{name}.");
                result.Options[name] = name == "set" && result.Options.TryGetValue(name, out var previous) ? previous + "\0" + value : value;
            }
        }
        return result;
    }
}

public static class Processes
{
    public static bool OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, name)) || OperatingSystem.IsWindows() && File.Exists(Path.Combine(p, name + ".exe")));
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string cwd)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            if (JevCredentials.Contains(arg)) throw new InvalidOperationException("Refusing to place JEV credentials in child-process arguments.");
            info.ArgumentList.Add(arg);
        }
        info.Environment.Remove(JevCredentials.EnvironmentVariable);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return info;
    }
    public static async Task<ProcessResult> Run(string exe, IEnumerable<string> args, string cwd, string? artifact = null, TimeSpan? timeout = null)
    {
        var info = StartInfo(exe, args, cwd);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var writer = artifact is null ? null : new StreamWriter(artifact, false, new UTF8Encoding(false));
        var gate = new SemaphoreSlim(1); var buffer = new StringBuilder(); bool overflow = false;
        async Task Drain(StreamReader reader)
        {
            var chars = new char[4096]; int count;
            while ((count = await reader.ReadAsync(chars, timer.Token)) > 0)
            {
                await gate.WaitAsync(timer.Token);
                try
                {
                    if (writer is not null) await writer.WriteAsync(chars.AsMemory(0, count), timer.Token);
                    else if (buffer.Length + count <= 16 * 1024 * 1024) buffer.Append(chars, 0, count);
                    else overflow = true;
                }
                finally { gate.Release(); }
            }
        }
        var reads = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try { await Task.WhenAll(reads, process.WaitForExitAsync(timer.Token)); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            return new(124, "Operation timed out; process tree terminated.");
        }
        if (overflow) throw new InvalidOperationException("Structured command output exceeded 16 MiB; narrow scope.");
        return new(process.ExitCode, buffer.ToString());
    }
}

public static class GitOwnershipMarkers
{
    // Stored in the repository's local Git config under branch.<name>.
    public const string BranchConfigKey = "sdeveng-owned";
    public const string BranchConfigValue = "true";

    // Stored in the linked worktree's private Git directory, never its checkout.
    public const string WorktreeFileName = "sdeveng-owned-worktree";
    public const string WorktreeFileContents = "sdeveng-owned-worktree-v1\n";
}

public static class Git
{
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
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("Git merge-tree could not forecast conflicts: " + Secrets.Redact(string.Join(' ', Output.Compact(result.Output, limits))));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var conflicts = lines.Where(line => line.StartsWith("CONFLICT ", StringComparison.Ordinal)).ToArray();
        var paths = conflicts.Select(line => Regex.Match(line, @"(?: in |delete/modify: )(.+?)(?: deleted|$)").Groups[1].Value.Trim()).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).Take(limits.MaxItems).ToArray();
        return new { schemaVersion = 1, kind = "git-conflict-forecast", baseRef, head, target, mergeBase, hasConflicts = result.ExitCode == 1, conflictCount = conflicts.Length, paths, pathsTruncated = conflicts.Length > paths.Length, evidence = conflicts.Take(limits.MaxItems), note = "Forecast only: refs, index and worktree were not changed. Rename and custom merge-driver behavior may differ in a real merge." };
    }
}
public record GitState(string Root, string Head, string? Branch, bool Clean, List<string> Operations, string[] Entries, (string name, string url, string direction)[] Remotes, GitUpstream? Upstream);
public record GitUpstream(string Name, int? Ahead, int? Behind);
public record GitChangedPaths(string[] Paths);
public record GitMergeBase(string Commit);
public record GitDivergence(int Ahead, int Behind);

public static class Repository
{
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
    public static string[] Discover(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".csproj" or ".fsproj" or ".vbproj").Order(StringComparer.Ordinal).ToArray();
    public static string[] Solutions(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".sln" or ".slnx").Order(StringComparer.Ordinal).ToArray();
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
    public static async Task<bool> IsTest(string root, string project) => (await Evaluate(root, project))["Properties"]?["IsTestProject"]?.GetValue<string>().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    public static async Task<Affected> Affected(string root, string[] changed)
    {
        root = Path.GetFullPath(root);
        changed = changed.Where(x => !SafeFiles.IsDiscoveryExcluded(x)).ToArray();
        var projects = Discover(root);
        if (changed.Length == 0) return new([], "No build-relevant changed files.");
        var broad = changed.Any(x => x.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x) is "global.json" or "NuGet.Config" or "nuget.config" || x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (broad) return new(projects, "Shared build, solution or project metadata changed; conservative full graph.");
        var selected = new HashSet<string>(StringComparer.Ordinal); var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var paths = changed.Select(x => Path.GetFullPath(x, root)).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var evaluation = await Evaluate(root, project);
            if (!string.IsNullOrEmpty(evaluation["Properties"]?["TargetFrameworks"]?.GetValue<string>())) return new(projects, "Multi-targeted graph; conservative full graph (conditional inner builds may differ).");
            var items = evaluation["Items"];
            var compiles = items?["Compile"]?.AsArray().Select(x => x?["FullPath"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            references[project] = items?["ProjectReference"]?.AsArray().Select(x => x?["FullPath"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            if (compiles.Any(paths.Contains) || paths.Any(p => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))) selected.Add(project);
        }
        // Removed linked files and custom build inputs cannot always be inferred from evaluated Compile items.
        if (paths.Any(p => !projects.Any(project => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
            return new(projects, "Change outside project directories; conservative full graph for custom or removed linked inputs.");
        bool added;
        do { added = false; foreach (var p in projects) if (references[p].Any(selected.Contains)) added |= selected.Add(p); } while (added);
        return new(selected.Order(StringComparer.Ordinal).ToArray(), "Evaluated Compile/ProjectReference graph including transitive dependents.");
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
        var projects = Discover(root); var references = new Dictionary<string, string[]>(StringComparer.Ordinal); var tests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in projects)
        {
            var evaluation = await Evaluate(root, candidate);
            references[candidate] = evaluation["Items"]?["ProjectReference"]?.AsArray().Select(x => Path.GetFullPath(x?["FullPath"]?.GetValue<string>() ?? "", root)).Where(File.Exists).ToArray() ?? [];
        }
        bool DependsOn(string candidate, string wanted, HashSet<string> visiting)
        {
            if (!visiting.Add(candidate)) return false;
            return references[candidate].Any(reference => string.Equals(reference, wanted, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || references.ContainsKey(reference) && DependsOn(reference, wanted, visiting));
        }
        foreach (var candidate in projects) if (await IsTest(root, candidate) && DependsOn(candidate, target, [])) tests.Add(candidate);
        return tests.Order(StringComparer.Ordinal).ToArray();
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
        var projects = Discover(root); var selected = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(project) }; var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var candidate in projects) references[candidate] = (await Evaluate(root, candidate))["Items"]?["ProjectReference"]?.AsArray().Select(x => Path.GetFullPath(x?["FullPath"]?.GetValue<string>() ?? "", root)).Where(File.Exists).ToArray() ?? [];
        bool added; do { added = false; foreach (var candidate in projects) if (references[candidate].Any(selected.Contains)) added |= selected.Add(candidate); } while (added);
        selected.Remove(Path.GetFullPath(project)); return selected.Order(StringComparer.Ordinal).ToArray();
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
public record Affected(string[] Projects, string Reason);

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
            if (new[] { ".git", ".agent-tool", ".agent-results", "bin", "obj", "node_modules", "artifacts", "TestResults" }.Contains(Path.GetFileName(dir)) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
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
public static class Secrets
{
    const string Pattern = @"(?i)(?:Bearer\s+[A-Za-z0-9._~+/=-]+|(?:api[_-]?key|password|secret|token)\s*[=:]\s*[^\s,;]+|-----BEGIN[^\r\n]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{16,})";
    public static string Redact(string value)
    {
        value = JevCredentials.Redact(value);
        return Regex.Replace(value, Pattern, "[REDACTED]");
    }
    public static bool LooksSensitive(string value) => !string.Equals(value, Redact(value), StringComparison.Ordinal);
    public static string RedactJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Output JSON is empty.");
        RedactNode(node);
        return node.ToJsonString(AgentTool.Json);
    }
    static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj.ToArray())
            {
                var name = Redact(entry.Key);
                if (name != entry.Key) { obj.Remove(entry.Key); obj[name] = entry.Value; }
                if (entry.Value is not null) RedactNode(entry.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.ToArray())
                if (item is not null) RedactNode(item);
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            value.ReplaceWith(JsonValue.Create(Redact(text)));
    }
}

public static class JevCredentials
{
    public const string EnvironmentVariable = "TYPESAFE_API_KEY";
    public static string Status(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return string.IsNullOrEmpty(environment(EnvironmentVariable)) ? "JEV credentials: unavailable" : "JEV credentials: configured";
    }
    internal static string? Read() => Environment.GetEnvironmentVariable(EnvironmentVariable);
    internal static bool IsConfigured(Func<string?> source) => !string.IsNullOrEmpty(source());
    internal static string Redact(string value)
    {
        var key = Read();
        return string.IsNullOrEmpty(key) ? value : value.Replace(key, "[REDACTED]", StringComparison.Ordinal);
    }
    internal static bool Contains(string value)
    {
        var key = Read();
        return !string.IsNullOrEmpty(key) && value.Contains(key, StringComparison.Ordinal);
    }
    internal static bool Authorize(HttpRequestMessage request, Func<string?> source)
    {
        var key = source();
        if (string.IsNullOrEmpty(key)) return false;
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return true; }
        catch (FormatException) { return false; }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record ToolkitSettings { public string[] OptionalTools { get; init; } = ["dotnet-trace", "dotnet-dump", "dotnet-counters", "dotnet-gcdump", "dotnet-monitor"]; public string[] EnabledIntegrations { get; init; } = []; public string Version { get; init; } = "0.0.0"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record OutputSettings { public int MaxLines { get; init; } = 12; public int MaxLineLength { get; init; } = 240; public int MaxItems { get; init; } = 30; public int MaxOutputChars { get; init; } = 16000; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record HealthSettings
{
    public bool RequireNullable { get; init; } = true;
    public bool RequireCentralPackages { get; init; } = true;
    public bool RequireDeterministic { get; init; } = true;
    public bool RequireAnalyzers { get; init; } = true;
    public bool RequireLockFiles { get; init; }
    public string[] AllowedFrameworks { get; init; } = ["net10.0"];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevCapabilityPolicy
{
    public bool Allowed { get; init; }
    public string[] Purposes { get; init; } = [];
    public int ExpectedCalls { get; init; }
    public int MaxCalls { get; init; }
    public bool DeterministicFirst { get; init; } = true;
    public int MaxInputBytes { get; init; } = 1;
    public int MaxCandidates { get; init; }
    public string Privacy { get; init; } = "not-applicable";
    public double MinConfidence { get; init; } = 1;
    public string Uncertainty { get; init; } = "review";
    public string GptEscalation { get; init; } = "normal";
    public void Validate(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || Purposes.Length == 0 || Purposes.Any(string.IsNullOrWhiteSpace) || ExpectedCalls < 0 || MaxCalls is < 0 or > 100 || ExpectedCalls > MaxCalls || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 0 or > 100 || MaxCandidates > MaxCalls || string.IsNullOrWhiteSpace(Privacy) || !double.IsFinite(MinConfidence) || MinConfidence is < 0 or > 1 || Uncertainty != "review" || GptEscalation is not ("normal" or "stronger") || Allowed != (MaxCalls > 0))
            throw new ArgumentException($"Invalid JEV capability policy: {capability}.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevSettings
{
    public string Mode { get; init; } = "auto";
    public string ApiUrl { get; init; } = "https://api.typesafe.ai/v1/systemone";
    public string Model { get; init; } = "jev-latest";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxInputBytes { get; init; } = 16384;
    public int MaxCandidates { get; init; } = 25;
    public double IncludeThreshold { get; init; } = .70;
    public double ExcludeThreshold { get; init; } = .10;
    public double MinConfidence { get; init; } = .80;
    public int CacheHours { get; init; } = 24;
    public Dictionary<string, JevCapabilityPolicy> Capabilities { get; init; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        if (Mode is not ("off" or "auto" or "required") || TimeoutSeconds is < 1 or > 120 || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 1 or > 100 || CacheHours is < 0 or > 720 || !double.IsFinite(IncludeThreshold) || !double.IsFinite(ExcludeThreshold) || !double.IsFinite(MinConfidence) || ExcludeThreshold < 0 || IncludeThreshold > 1 || ExcludeThreshold >= IncludeThreshold || MinConfidence is < 0 or > 1) throw new ArgumentException("Invalid JEV configuration.");
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) throw new ArgumentException("JEV endpoint must use HTTPS without credentials, query or fragment.");
        foreach (var (capability, policy) in Capabilities) policy.Validate(capability);
    }
}
public record Settings(JevSettings Jev, OutputSettings Output, HealthSettings Health, ToolkitSettings Toolkit)
{
    public static void AddConfigurationSources(IConfigurationManager configuration, string toolkit)
    {
        foreach (var name in new[] { "jev", "output-limits", "repo-health", "toolkit" })
            configuration.AddJsonFile(Path.Combine(toolkit, "config", name + ".json"), optional: false, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
    }
    public static void RegisterOptions(IServiceCollection services, IConfiguration configuration, string toolkit)
    {
        services.AddOptions<AgentTool.RuntimeSettingsOptions>()
            .Configure(options => { try { options.Settings = Load(toolkit, name => configuration[name]); } catch (ArgumentException e) { options.ValidationError = e.Message; } })
            .Validate(options => options.ValidationError is null, "Invalid toolkit settings.")
            .Validate(options => options.Settings.Jev.Mode is "off" or "auto" or "required", "Invalid JEV settings.")
            .Validate(options => options.Settings.Output.MaxLines is >= 1 and <= 100 && options.Settings.Output.MaxLineLength is >= 20 and <= 2000 && options.Settings.Output.MaxItems is >= 1 and <= 200 && options.Settings.Output.MaxOutputChars is >= 1024 and <= 131072, "Invalid output settings.")
            .ValidateOnStart();
    }
    public static Settings Load(string toolkit, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        T Read<T>(string name) where T : new()
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "config", name + ".json")))?.AsObject() ?? throw new ArgumentException($"Empty configuration: {name}.json");
            node.Remove("$schema");
            try { return node.Deserialize<T>(AgentTool.Json) ?? throw new ArgumentException($"Empty configuration: {name}.json"); }
            catch (JsonException e) { throw new ArgumentException($"Invalid configuration {name}.json: {e.Message}"); }
        }
        var jev = Read<JevSettings>("jev");
        jev = jev with { Mode = env("JEV_MODE") ?? jev.Mode, ApiUrl = env("TYPESAFE_API_URL") ?? jev.ApiUrl, Model = env("JEV_MODEL") ?? jev.Model, TimeoutSeconds = env("JEV_TIMEOUT_SECONDS") is { } timeout ? int.TryParse(timeout, out var seconds) ? seconds : throw new ArgumentException("Invalid JEV_TIMEOUT_SECONDS.") : jev.TimeoutSeconds };
        jev.Validate();
        var output = Read<OutputSettings>("output-limits");
        if (output.MaxLines is < 1 or > 100 || output.MaxLineLength is < 20 or > 2000 || output.MaxItems is < 1 or > 200 || output.MaxOutputChars is < 1024 or > 131072) throw new ArgumentException("Invalid output limits.");
        return new(jev, output, Read<HealthSettings>("repo-health"), Read<ToolkitSettings>("toolkit"));
    }
    public static Settings LoadFor(string toolkit, string command, Settings configured)
        => command is "install" or "update" or "uninstall" or "validate" or "release" or "results init" or "results new" or "results list" or "results latest" or "results context" or "results clean" or "upstream status" or "upstream update" or "upstream dotnet-skills"
            ? new(new(), new(), new(), new()) : configured;
}

public sealed class JevClient
{
    readonly HttpClient http;
    readonly JevSettings settings;
    readonly string cacheDirectory;
    readonly Func<string?> credentialSource;
    public JevClient(HttpClient http, JevSettings settings, string cacheDirectory) : this(http, settings, cacheDirectory, JevCredentials.Read) { }
    internal JevClient(HttpClient http, JevSettings settings, string cacheDirectory, Func<string?> credentialSource)
    {
        this.http = http; this.settings = settings; this.cacheDirectory = cacheDirectory; this.credentialSource = credentialSource;
    }
    static readonly JevCapabilityPolicy DefaultTestPolicy = new() { Allowed = true, Purposes = ["candidate-relevance"], ExpectedCalls = 0, MaxCalls = 1, MaxInputBytes = 16384, MaxCandidates = 1, Privacy = "sanitized-bounded-text", MinConfidence = .8 };
    public static JsonObject Request(string kind, string state, string instructions, JsonNode? criteria, string model)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("state and instructions are required.");
        if (kind is not ("noul" or "choice" or "score")) throw new ArgumentException("Unsupported judgment type.");
        if (kind == "choice" && (criteria is not JsonObject choice || choice.Count is < 2 or > 255)) throw new ArgumentException("Choice requires a criteria object with 2–255 named choices.");
        if (kind == "score" && (criteria is not JsonArray score || score.Count is < 2 or > 10)) throw new ArgumentException("Score requires 2–10 ordered criteria.");
        var question = new JsonObject { ["type"] = kind, ["instructions"] = instructions };
        if (criteria is not null) question["criteria"] = criteria.DeepClone();
        return new JsonObject { ["model"] = model, ["state"] = state, ["questions"] = new JsonObject { ["judgment"] = question } };
    }
    public static string Hash(JsonNode request, string endpoint)
    {
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Canonical(x.Value)))),
            JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("jev-v1\n" + endpoint + "\n" + Canonical(request)!.ToJsonString())));
    }
    public static string Route(double probability, JevSettings settings) => !double.IsFinite(probability) || probability is < 0 or > 1 ? "REVIEW" : probability >= settings.IncludeThreshold ? "INCLUDE" : probability <= settings.ExcludeThreshold ? "EXCLUDE" : "REVIEW";
    public async Task<Result> Judge(JsonObject request, JevCapabilityPolicy? policy = null, string purpose = "candidate-relevance", string capability = "relevance")
    {
        policy ??= DefaultTestPolicy;
        settings.Validate();
        policy.Validate("invocation");
        if (!policy.Allowed || policy.MaxCalls < 1) return Decision("REVIEW", new { reason = "JEV capability is disallowed." }, policy, capability, purpose, null, false, 0, "GPT", "policy-disallowed");
        if (!policy.Purposes.Contains(purpose, StringComparer.Ordinal)) return Decision("REVIEW", new { reason = "JEV purpose is not allowed for this capability." }, policy, capability, "unrecognized", null, false, 0, "GPT", "purpose-disallowed");
        if (settings.Mode == "off") return Fallback("JEV disabled.", policy, capability, purpose, 0);
        if (!JevCredentials.IsConfigured(credentialSource)) return Fallback("JEV credentials unavailable.", policy, capability, purpose, 0);
        var body = request.ToJsonString();
        if (Encoding.UTF8.GetByteCount(body) > Math.Min(settings.MaxInputBytes, policy.MaxInputBytes) || Secrets.LooksSensitive(body)) return Fallback("Input too large or potentially sensitive.", policy, capability, purpose, 0);
        var hash = Hash(request, settings.ApiUrl); var cache = Path.Combine(cacheDirectory, hash + ".json");
        var remoteCalls = 0;
        try
        {
            SafeFiles.NoLinks(cache);
            if (settings.CacheHours > 0 && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(settings.CacheHours))
            {
                try { return Parse(JsonNode.Parse(await File.ReadAllTextAsync(cache))!, request, true, policy, purpose, capability: capability); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException) { /* Invalid cache is ignored; no guessed decisions. */ }
            }
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl);
            if (!JevCredentials.Authorize(message, credentialSource)) return Fallback("JEV credentials unavailable.", policy, capability, purpose, 0);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            remoteCalls = 1;
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return Fallback($"HTTP {(int)response.StatusCode}; response body withheld.", policy, capability, purpose, remoteCalls);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream(); var block = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(block, timeout.Token)) > 0)
            { if (memory.Length + count > 65536) return Fallback("Response too large.", policy, capability, purpose, remoteCalls); await memory.WriteAsync(block.AsMemory(0, count), timeout.Token); }
            var json = JsonNode.Parse(memory.ToArray()) ?? throw new JsonException();
            var parsed = Parse(json, request, false, policy, purpose, remoteCalls, capability);
            if (settings.CacheHours > 0) { try { SafeFiles.Atomic(cache, CacheResponse(json, request).ToJsonString()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Cache is an optimization only. */ } }
            return parsed;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { return Fallback("JEV unavailable or invalid response; no candidate discarded.", policy, capability, purpose, remoteCalls); }
    }
    Result Fallback(string reason, JevCapabilityPolicy policy, string capability, string purpose, int remoteCalls)
        => Decision("REVIEW", new { reason, requiredFailed = settings.Mode == "required" }, policy, capability, purpose, null, false, remoteCalls, "GPT", "fallback", settings.Mode == "required" ? 3 : 0);

    static Result Decision(string status, object judgment, JevCapabilityPolicy policy, string capability, string purpose, double? confidence, bool cached, int remoteCalls, string? fallbackTarget = null, string? escalationReason = null, int exitCode = 0)
    {
        var uncertain = status == "REVIEW";
        return new(status, new
        {
            judgment,
            instrumentation = new
            {
                schemaVersion = 1,
                capability,
                purpose,
                privacy = policy.Privacy,
                budget = new { expectedCalls = policy.ExpectedCalls, maxCalls = policy.MaxCalls },
                bounds = new { policy.DeterministicFirst, policy.MaxInputBytes, policy.MaxCandidates },
                counts = new { invocations = 1, remoteCalls, cacheHits = cached ? 1 : 0, fallbacks = fallbackTarget is null ? 0 : 1, escalations = uncertain ? 1 : 0 },
                confidence = new { reported = confidence, minimum = policy.MinConfidence, uncertain },
                fallback = new { used = fallbackTarget is not null, target = fallbackTarget },
                escalation = new { required = uncertain, target = uncertain ? policy.GptEscalation + "-gpt" : null, reason = escalationReason ?? (uncertain ? "uncertain-judgment" : null) },
                contextAvoidedBytes = 0,
                payloadCaptured = false
            }
        }, exitCode);
    }
    public static Result PolicyReview(JevCapabilityPolicy? policy, string capability, string purpose, string reason)
    {
        var knownPolicy = policy is not null;
        policy ??= new() { Allowed = false, Purposes = ["unrecognized"], ExpectedCalls = 0, MaxCalls = 0, MaxInputBytes = 1, MaxCandidates = 0, Privacy = "not-transmitted", MinConfidence = 1 };
        var safePurpose = knownPolicy && policy.Purposes.Contains(purpose, StringComparer.Ordinal) ? purpose : "unrecognized";
        return Decision("REVIEW", new { reason }, policy, knownPolicy ? capability : "unrecognized", safePurpose, null, false, 0, "GPT", "policy-gate");
    }
    static JsonObject CacheResponse(JsonNode response, JsonObject request)
    {
        var answer = response["answers"]!["judgment"]!; var kind = request["questions"]!["judgment"]!["type"]!.GetValue<string>();
        var cached = new JsonObject { ["type"] = kind };
        if (kind == "noul") cached["noul"] = answer["noul"]!.DeepClone();
        else
        {
            cached["confidence"] = answer["confidence"]!.DeepClone();
            cached["probabilities"] = answer["probabilities"]!.DeepClone();
            cached[kind] = answer[kind]!.DeepClone();
            if (kind == "score") cached["legend"] = answer["legend"]!.DeepClone();
        }
        return new JsonObject { ["answers"] = new JsonObject { ["judgment"] = cached } };
    }
    public Result Parse(JsonNode response, JsonObject request, bool cached, JevCapabilityPolicy? policy = null, string purpose = "candidate-relevance", int remoteCalls = 0, string capability = "relevance")
    {
        policy ??= DefaultTestPolicy;
        var q = request["questions"]!["judgment"]!; var kind = q["type"]!.GetValue<string>();
        var a = response["answers"]?["judgment"] ?? throw new JsonException("Missing answer.");
        if (a["type"]?.GetValue<string>() != kind) throw new JsonException("Wrong answer type.");
        double Number(string name, double max = 1)
        {
            var n = a[name]?.GetValue<double>() ?? throw new JsonException("Missing numeric answer.");
            if (!double.IsFinite(n) || n < 0 || n > max) throw new JsonException("Invalid numeric range."); return n;
        }
        if (kind == "noul") { var n = Number("noul"); var status = Route(n, settings); return Decision(status, new { probability = n, cached }, policy, capability, purpose, null, cached, remoteCalls, null, status == "REVIEW" ? "uncertain-judgment" : null); }
        var confidence = Number("confidence"); var probabilities = a["probabilities"]?.AsObject() ?? throw new JsonException("Missing probability distribution.");
        var expected = kind == "choice" ? q["criteria"]!.AsObject().Select(x => x.Key).ToArray() : Enumerable.Range(0, q["criteria"]!.AsArray().Count).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (!expected.Order(StringComparer.Ordinal).SequenceEqual(probabilities.Select(x => x.Key).Order(StringComparer.Ordinal))) throw new JsonException("Wrong probability labels.");
        var values = probabilities.Select(x => x.Value is JsonValue value && value.TryGetValue<double>(out var probability) ? probability : throw new JsonException("Invalid probability value.")).ToArray();
        if (values.Any(x => !double.IsFinite(x) || x is < 0 or > 1) || Math.Abs(values.Sum() - 1) > .01) throw new JsonException("Invalid probability distribution.");
        object value;
        if (kind == "choice")
        {
            var choice = a["choice"]?.GetValue<string>() ?? throw new JsonException("Missing choice.");
            if (!expected.Contains(choice) || probabilities[choice] is not JsonValue choiceProbability || !choiceProbability.TryGetValue<double>(out var selected) || selected + .001 < values.Max()) throw new JsonException("Invalid choice.");
            value = choice;
        }
        else
        {
            var score = Number("score", expected.Length - 1);
            var legend = a["legend"]?.AsObject() ?? throw new JsonException("Missing score legend.");
            if (!expected.Order(StringComparer.Ordinal).SequenceEqual(legend.Select(x => x.Key).Order(StringComparer.Ordinal)) || legend.Any(x => x.Value is not JsonValue value || !value.TryGetValue<string>(out _))) throw new JsonException("Invalid score legend.");
            var weighted = probabilities.Sum(x => int.Parse(x.Key, CultureInfo.InvariantCulture) * (x.Value is JsonValue probability && probability.TryGetValue<double>(out var number) ? number : throw new JsonException("Invalid probability value.")));
            if (Math.Abs(score - weighted) > .02) throw new JsonException("Score and distribution disagree.");
            value = score;
        }
        var result = confidence >= policy.MinConfidence ? "ACCEPT" : "REVIEW";
        return Decision(result, new { value, confidence, cached }, policy, capability, purpose, confidence, cached, remoteCalls, null, result == "REVIEW" ? "low-confidence" : null);
    }
}

public record InstallEntry(string Destination, string Source, bool Directory);
public record InstallManifest(string Toolkit, string Home, string CodexHome, List<InstallEntry> Entries);
public static class Installer
{
    static string ManifestPath(string codex) => Path.Combine(codex, "sdeveng-install.json");
    static string LegacyManifestPath(string codex) => Path.Combine(codex, "codex-toolkit-install.json");
    static string? LinkTarget(InstallEntry entry)
    {
        FileSystemInfo info = entry.Directory ? new DirectoryInfo(entry.Destination) : new FileInfo(entry.Destination);
        try
        {
            if (info.ResolveLinkTarget(false) is { } resolved) return resolved.FullName;
        }
        catch (IOException) { }
        var target = info.LinkTarget;
        return target is null ? null : Path.GetFullPath(target, Path.GetDirectoryName(entry.Destination)!);
    }
    static bool Exists(InstallEntry entry) => File.Exists(entry.Destination) || Directory.Exists(entry.Destination) || LinkTarget(entry) is not null;
    static bool Matches(InstallEntry e)
    {
        var target = LinkTarget(e);
        return target is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(e.Source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    static void DeleteLink(InstallEntry entry)
    {
        if (entry.Directory && (OperatingSystem.IsWindows() || Directory.Exists(entry.Destination))) Directory.Delete(entry.Destination);
        else File.Delete(entry.Destination);
    }
    static List<InstallEntry> Plan(string toolkit, string home, string codex, bool bin)
    {
        if (bin && OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--bin is not supported on Windows; invoke `dotnet <toolkit>/tools/AgentTool.cs` directly.");
        var entries = new List<InstallEntry> { new(Path.Combine(codex, "AGENTS.md"), Path.Combine(toolkit, "global/AGENTS.md"), false) };
        entries.AddRange(Directory.GetFiles(Path.Combine(toolkit, "agents"), "*.toml").Select(s => new InstallEntry(Path.Combine(codex, "agents", Path.GetFileName(s)), s, false)));
        entries.AddRange(Directory.GetDirectories(Path.Combine(toolkit, "plugins/sdeveng/skills")).Select(s => new InstallEntry(Path.Combine(home, ".agents/skills", Path.GetFileName(s)), s, true)));
        if (bin)
        {
            entries.Add(new(Path.Combine(home, ".local/bin/sdeveng"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
            entries.Add(new(Path.Combine(home, ".local/bin/codex-agent-tool"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
        }
        return entries;
    }
    static bool IsOwnedShape(InstallEntry entry, string toolkit, string home, string codex)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static bool Under(string path, string root, StringComparison comparison) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
        var destination = Path.GetFullPath(entry.Destination); var source = Path.GetFullPath(entry.Source);
        if (!Under(source, toolkit, comparison)) return false;
        return destination == Path.Combine(codex, "AGENTS.md") && source == Path.Combine(toolkit, "global", "AGENTS.md") && !entry.Directory
            || Under(destination, Path.Combine(codex, "agents"), comparison) && destination.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && !entry.Directory
            || Under(destination, Path.Combine(home, ".agents", "skills"), comparison) && entry.Directory
            || (destination == Path.Combine(home, ".local", "bin", "sdeveng") || destination == Path.Combine(home, ".local", "bin", "codex-agent-tool")) && source == Path.Combine(toolkit, "tools", "AgentTool.cs") && !entry.Directory;
    }
    static InstallManifest? Read(string path)
    {
        SafeFiles.NoLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), AgentTool.Json) ?? throw new IOException("Invalid installation manifest.") : null;
    }
    public static object Inspect(string codex)
    {
        var manifest = Read(ManifestPath(codex));
        var legacy = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        return manifest is null && legacy is null ? new { installed = false } : new { installed = true, migrationRequired = legacy is not null, entries = (manifest ?? legacy)!.Entries.Select(e => new { e.Destination, healthy = Matches(e) && (File.Exists(e.Source) || Directory.Exists(e.Source)) }) };
    }
    public static Result Run(string toolkit, string home, string? codexHome, string command, bool dryRun, bool bin)
    {
        toolkit = Path.GetFullPath(toolkit); home = Path.GetFullPath(home); var codex = Path.GetFullPath(codexHome ?? Path.Combine(home, ".codex"));
        SafeFiles.NoLinks(codex);
        var manifestPath = ManifestPath(codex);
        var manifest = Read(manifestPath);
        var legacyManifest = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        var migratingLegacy = legacyManifest is not null;
        manifest ??= legacyManifest;
        if (manifest is not null && (manifest.Toolkit != toolkit || manifest.Home != home || manifest.CodexHome != codex)) throw new IOException("Installation belongs to a different checkout/home; use that checkout to uninstall first.");
        if (manifest is not null && manifest.Entries.Any(e => !IsOwnedShape(e, toolkit, home, codex))) throw new IOException("Ownership manifest contains unexpected paths; no changes made.");
        var plan = command == "uninstall" ? [] : Plan(toolkit, home, codex, bin || manifest?.Entries.Any(x => x.Destination == Path.Combine(home, ".local/bin/codex-agent-tool")) == true);
        var removals = command == "uninstall" ? manifest?.Entries.ToList() ?? [] : command == "update" || migratingLegacy ? manifest?.Entries.Except(plan).ToList() ?? [] : [];
        var conflicts = plan.Where(e => Exists(e) && !(manifest?.Entries.Any(existing => existing.Destination == e.Destination && Matches(existing)) == true)).Select(e => e.Destination).ToArray();
        if (command != "uninstall" && conflicts.Length > 0) return new("conflict", new { conflicts, changed = false }, 1);
        foreach (var entry in plan.Concat(removals)) SafeFiles.NoLinks(Path.GetDirectoryName(entry.Destination)!);
        var preservedRemovals = removals.Where(e => Exists(e) && !Matches(e)).Select(e => e.Destination).ToArray();
        if (dryRun) return Result.Ok(new { dryRun, command, migratingLegacy, plan, removals, preserved = conflicts.Concat(preservedRemovals) });
        if (plan.Count == 0 && removals.Count == 0) return Result.Ok(new { command, changed = 0 });
        Directory.CreateDirectory(codex);
        var lockPath = Path.Combine(codex, "sdeveng-install.lock"); SafeFiles.NoLinks(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        // Re-read under the lock so stale concurrent plans cannot overwrite ownership records.
        var current = Read(migratingLegacy ? LegacyManifestPath(codex) : manifestPath);
        if (JsonSerializer.Serialize(current, AgentTool.Json) != JsonSerializer.Serialize(manifest, AgentTool.Json)) throw new IOException("Installation changed concurrently; rerun command.");
        manifest ??= new(toolkit, home, codex, []);
        var changed = new List<string>(); var preserved = new List<string>();
        foreach (var entry in removals)
        {
            if (Matches(entry)) { DeleteLink(entry); changed.Add(entry.Destination); }
            else if (Exists(entry)) preserved.Add(entry.Destination);
            manifest.Entries.Remove(entry);
            SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json));
        }
        foreach (var entry in plan)
        {
            if (manifest.Entries.Contains(entry) && Matches(entry)) continue;
            if (Exists(entry)) throw new IOException("Destination appeared during installation; rerun to inspect conflicts.");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
            if (entry.Directory) Directory.CreateSymbolicLink(entry.Destination, entry.Source); else File.CreateSymbolicLink(entry.Destination, entry.Source);
            manifest.Entries.Remove(entry);
            manifest.Entries.Add(entry);
            try { SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json)); }
            catch { if (Matches(entry)) DeleteLink(entry); throw; }
            changed.Add(entry.Destination);
        }
        if (migratingLegacy || command == "uninstall") File.Delete(LegacyManifestPath(codex));
        if (command == "uninstall") File.Delete(manifestPath);
        return Result.Ok(new { command, migratingLegacy, changed, preserved, note = "Only owned links changed; user replacements are preserved. Empty parent directories remain." });
    }
}

public static class RuntimeReferences
{
    static readonly Regex MarkdownLink = new(@"\]\(([^)]+)\)", RegexOptions.Compiled);
    static readonly Regex SkillReference = new(@"(?<![A-Za-z0-9_./-])(references/[A-Za-z0-9_./-]+\.md)\b", RegexOptions.Compiled);

    public static string[] Missing(string root)
    {
        var plugin = Path.Combine(root, "plugins", "sdeveng");
        var skills = Path.Combine(plugin, "skills");
        if (!Directory.Exists(skills)) return ["Missing runtime skills directory: plugins/sdeveng/skills"];
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var markdown = SafeFiles.Enumerate(plugin).Where(file => file.EndsWith(".md", StringComparison.Ordinal)
            && (Path.GetFileName(file) == "SKILL.md" || file.Contains(Path.DirectorySeparatorChar + "references" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        foreach (var file in markdown)
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "SKILL.md")
                foreach (Match match in SkillReference.Matches(text)) Check(file, match.Groups[1].Value, root, errors);
            foreach (Match match in MarkdownLink.Matches(text))
            {
                var target = match.Groups[1].Value.Split('#')[0].Trim('<', '>');
                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                Check(file, target, root, errors);
            }
        }
        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    static void Check(string source, string target, string root, HashSet<string> errors)
    {
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(source)!);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            errors.Add($"Missing runtime reference from {Path.GetRelativePath(root, source)}: {target}");
    }
}

public static class PluginManifests
{
    public const string PortableSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    public const string PluginName = "sdeveng-engineering-toolkit";

    public static JsonObject CompatibilityFrom(JsonObject portable) => new()
    {
        ["name"] = portable["name"]?.DeepClone(),
        ["version"] = portable["version"]?.DeepClone(),
        ["description"] = portable["description"]?.DeepClone(),
        ["skills"] = "./skills/",
        ["interface"] = portable["extensions"]?["com.openai"]?["interface"]?.DeepClone()
    };

    public static void Validate(string root, List<string> errors)
    {
        var pluginRoot = Path.Combine(root, "plugins", "sdeveng");
        try
        {
            var portable = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, "plugin.json"))) as JsonObject;
            var compatibility = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, ".codex-plugin", "plugin.json"))) as JsonObject;
            if (portable is null || compatibility is null) { errors.Add("Plugin manifests must be JSON objects."); return; }
            if (portable["$schema"]?.GetValue<string>() != PortableSchema || portable["name"]?.GetValue<string>() != PluginName)
                errors.Add("Portable plugin schema or stable identity mismatch.");
            if (portable["author"]?["name"]?.GetValue<string>() != "SimplexiDev Engineering Toolkit"
                || portable["author"]?["url"]?.GetValue<string>() != "https://github.com/simplexidev"
                || portable["homepage"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng"
                || portable["repository"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng")
                errors.Add("Portable plugin publisher metadata mismatch.");
            if (portable["extensions"]?["com.openai"]?["interface"] is not JsonObject)
                errors.Add("Portable plugin OpenAI extension metadata missing.");
            if (!JsonNode.DeepEquals(compatibility, CompatibilityFrom(portable)))
                errors.Add("Codex compatibility manifest must be derived from the portable manifest.");
            if (!Directory.Exists(Path.Combine(pluginRoot, "skills")) || !Directory.GetDirectories(Path.Combine(pluginRoot, "skills")).Any())
                errors.Add("Portable plugin skill discovery directory is missing or empty.");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            errors.Add("Unable to validate plugin manifests: " + e.Message);
        }
    }
}

public static class Validation
{
    public static Result Run(string root)
    {
        var errors = new List<string>(); int parsed = 0;
        foreach (var file in SafeFiles.Enumerate(root))
        {
            if (new FileInfo(file).Length == 0) errors.Add($"Empty file: {Path.GetRelativePath(root, file)}");
            if (Path.GetExtension(file) == ".json")
                try { JsonNode.Parse(File.ReadAllText(file)); parsed++; } catch (JsonException) { errors.Add($"Invalid JSON: {file}"); }
        }
        foreach (var file in Directory.GetFiles(Path.Combine(root, "config"), "*.json").Concat(Directory.GetFiles(Path.Combine(root, "upstream"), "*.json")))
        {
            var instance = JsonNode.Parse(File.ReadAllText(file))!;
            var schemaReference = instance["$schema"]?.GetValue<string>();
            if (schemaReference is null) { errors.Add($"Missing schema: {Path.GetRelativePath(root, file)}"); continue; }
            if (!Uri.TryCreate(schemaReference, UriKind.Absolute, out _))
            {
                var schemaPath = Path.GetFullPath(schemaReference, Path.GetDirectoryName(file)!);
                if (!File.Exists(schemaPath)) errors.Add($"Missing schema file: {Path.GetRelativePath(root, file)}");
                else ValidateSchema(instance, JsonNode.Parse(File.ReadAllText(schemaPath))!, Path.GetRelativePath(root, file), errors);
            }
        }
        foreach (var skill in Directory.GetDirectories(Path.Combine(root, "plugins/sdeveng/skills")))
        {
            var path = Path.Combine(skill, "SKILL.md");
            if (!File.Exists(path)) { errors.Add($"Missing skill: {skill}"); continue; }
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---\n", StringComparison.Ordinal) || !text.Contains("\nname: " + Path.GetFileName(skill) + "\n", StringComparison.Ordinal) || !text.Contains("\ndescription: ", StringComparison.Ordinal)) errors.Add($"Invalid skill frontmatter: {skill}");
            if (!File.Exists(Path.Combine(root, "evals", Path.GetFileName(skill), "eval.yaml"))) errors.Add($"Missing evaluation: {skill}");
            var ui = Path.Combine(skill, "agents", "openai.yaml");
            if (!File.Exists(ui)) errors.Add($"Missing skill UI metadata: {skill}");
            else
            {
                var uiText = File.ReadAllText(ui); var name = Path.GetFileName(skill);
                if (!Regex.IsMatch(uiText, @"(?m)^interface:\s*$") || !Regex.IsMatch(uiText, @"(?m)^\s+display_name:\s+\S") || !Regex.IsMatch(uiText, @"(?m)^\s+short_description:\s+\S") || !uiText.Contains("$" + name, StringComparison.Ordinal)) errors.Add($"Invalid skill UI metadata: {skill}");
            }
        }
        errors.AddRange(RuntimeReferences.Missing(root));
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var native in Directory.GetFiles(Path.Combine(root, "agents"), "*.toml"))
        {
            var nativeText = File.ReadAllText(native);
            var name = Regex.Match(nativeText, "(?m)^name\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var model = Regex.Match(nativeText, "(?m)^model\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var effort = Regex.Match(nativeText, "(?m)^model_reasoning_effort\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            if (name.Length == 0 || !nativeNames.Add(name) || model is not ("gpt-5.6-luna" or "gpt-5.6-terra" or "gpt-5.6-sol" or "gpt-6-astra" or "gpt-5.5") || effort is not ("low" or "medium" or "high" or "xhigh" or "max" or "ultra")) errors.Add($"Invalid native agent metadata: {native}");
        }
        PluginManifests.Validate(root, errors);
        var version = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/toolkit.json")))?["version"]?.GetValue<string>();
        var portableVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/plugin.json")))?["version"]?.GetValue<string>();
        var compatibilityVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/.codex-plugin/plugin.json")))?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || portableVersion != version || compatibilityVersion != version) errors.Add("Toolkit and plugin manifest versions must match.");
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".agents/plugins/marketplace.json")))!;
        if (marketplace["plugins"]?[0]?["source"]?["path"]?.GetValue<string>() != "./plugins/sdeveng"
            || marketplace["plugins"]?[0]?["name"]?.GetValue<string>() != PluginManifests.PluginName) errors.Add("Marketplace source path or plugin identity mismatch.");
        var ecosystem = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/ecosystem.json")))!;
        if (ecosystem["pluginPath"]?.GetValue<string>() != "plugins/sdeveng"
            || ecosystem["templatePath"]?.GetValue<string>() != "templates/project"
            || ecosystem["siblingRepositoriesAreRuntimeDependencies"]?.GetValue<bool>() != false
            || Directory.GetDirectories(Path.Combine(root, "plugins")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1
            || Directory.GetDirectories(Path.Combine(root, "templates")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1)
            errors.Add("Ecosystem plugin/template topology mismatch.");
        try { Settings.Load(root, _ => null); } catch (ArgumentException e) { errors.Add(e.Message); }
        return new(errors.Count == 0 ? "ok" : "failed", new { jsonFiles = parsed, errors, note = "Structural validation includes configuration schemas, runtime references and release identity." }, errors.Count == 0 ? 0 : 1);
    }
    static void ValidateSchema(JsonNode instance, JsonNode schema, string file, List<string> errors)
    {
        if (schema["type"]?.GetValue<string>() == "object")
        {
            if (instance is not JsonObject value) { errors.Add($"Schema type mismatch: {file} must be object."); return; }
            var properties = schema["properties"]?.AsObject() ?? [];
            foreach (var required in schema["required"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []) if (!value.ContainsKey(required)) errors.Add($"Schema required key missing: {file}:{required}");
            var additional = schema["additionalProperties"];
            if (additional is JsonValue additionalValue && additionalValue.TryGetValue<bool>(out var allowed) && !allowed)
                foreach (var key in value.Select(x => x.Key)) if (!properties.ContainsKey(key)) errors.Add($"Schema unknown key: {file}:{key}");
            foreach (var property in properties) if (value[property.Key] is { } child) ValidateSchema(child, property.Value!, file + ":" + property.Key, errors);
            if (additional is JsonObject additionalSchema)
                foreach (var property in value.Where(x => !properties.ContainsKey(x.Key) && x.Value is not null)) ValidateSchema(property.Value!, additionalSchema, file + ":" + property.Key, errors);
        }
        else if (schema["type"]?.GetValue<string>() == "array")
        {
            if (instance is not JsonArray values) { errors.Add($"Schema type mismatch: {file} must be array."); return; }
            foreach (var value in values.Where(x => x is not null)) ValidateSchema(value!, schema["items"]!, file + "[]", errors);
        }
        else if (schema["type"]?.GetValue<string>() is { } type && !MatchesType(instance, type)) errors.Add($"Schema type mismatch: {file} must be {type}.");
    }
    static bool MatchesType(JsonNode node, string type) => type switch { "string" => node is JsonValue v && v.TryGetValue<string>(out _), "boolean" => node is JsonValue v && v.TryGetValue<bool>(out _), "integer" => node is JsonValue v && v.TryGetValue<int>(out _), "number" => node is JsonValue v && v.TryGetValue<double>(out _), _ => true };
}

public static class Evaluation
{
    public static Result Run(string toolkit, string? skill, string? resultsPath)
    {
        // Evaluation documents use JSON syntax, a strict YAML 1.2 subset, to stay dependency-free.
        var cases = Directory.GetFiles(Path.Combine(toolkit, "evals"), "eval.yaml", SearchOption.AllDirectories).Where(p => skill is null || Path.GetFileName(Path.GetDirectoryName(p)) == skill).ToArray();
        if (cases.Length == 0) throw new ArgumentException("No matching evaluation.");
        var runs = resultsPath is null ? null : JsonNode.Parse(File.ReadAllText(resultsPath))?.AsArray();
        var outcomes = new List<object>(); bool failed = false;
        foreach (var path in cases)
        {
            var specification = JsonNode.Parse(File.ReadAllText(path))!;
            var name = specification["skill"]!.GetValue<string>();
            if (runs is null)
            {
                var fixturePath = Path.GetFullPath(specification["fixture"]!.GetValue<string>(), Path.GetDirectoryName(path)!);
                var fixture = JsonNode.Parse(File.ReadAllText(fixturePath));
                var ok = fixture is JsonObject && specification["scenario"]?.GetValue<string>().Length > 10 && specification["expected"]?.GetValue<string>().Length > 10 && specification["safety"]?.GetValue<string>().Length > 10;
                failed |= !ok;
                outcomes.Add(new { skill = name, passed = ok, kind = "scenario/fixture integrity", agentBehaviorMeasured = false });
                continue;
            }
            var run = runs.SingleOrDefault(r => r?["skill"]?.GetValue<string>() == name);
            var failures = new List<string>();
            if (run is null) failures.Add("Missing run.");
            else
            {
                if (run["success"]?.GetValue<bool>() != true) failures.Add("Correctness failed.");
                if (run["expectedSatisfied"]?.GetValue<bool>() != true || run["safetySatisfied"]?.GetValue<bool>() != true) failures.Add("Trusted grader must attest expected and safety behavior.");
                if (string.IsNullOrWhiteSpace(run["revision"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["model"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["promptHash"]?.GetValue<string>())) failures.Add("Measured run requires revision, model and promptHash provenance.");
                foreach (var budget in specification["budgets"]!.AsObject())
                    if (run[budget.Key] is null || run[budget.Key]!.GetValue<double>() < 0 || run[budget.Key]!.GetValue<double>() > budget.Value!.GetValue<double>()) failures.Add($"Missing, invalid or exceeded {budget.Key}.");
                if (run["baseline"] is not JsonObject baseline || baseline["success"]?.GetValue<bool>() != true) failures.Add("Successful baseline required for comparison.");
                else if (baseline["tokens"] is null || baseline["tokens"]!.GetValue<double>() <= 0) failures.Add("Valid baseline token measurement required.");
                else if (run["tokens"] is not null && run["tokens"]!.GetValue<double>() > baseline["tokens"]!.GetValue<double>() * 1.10) failures.Add("Token use regressed more than 10% against baseline.");
            }
            failed |= failures.Count > 0;
            outcomes.Add(new { skill = name, passed = failures.Count == 0, failures });
        }
        return new(failed ? "failed" : "ok", new { outcomes, note = runs is null ? "Offline fixture integrity only; it does not claim agent behavior. Supply trusted, attested --results for measured regression gates." : "Attested measured runs checked against correctness, safety, provenance and regression budgets." }, failed ? 1 : 0);
    }
}
