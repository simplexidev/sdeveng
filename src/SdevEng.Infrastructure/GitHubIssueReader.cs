using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

public sealed record GitHubIssue(
    int Number, string Title, string State, Uri HtmlUrl, string? Body, string? Author);

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
