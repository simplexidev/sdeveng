using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

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
