using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

public sealed record ConfiguredGitHubLabel(string Name, string Description, string Color);
public sealed record GitHubLabelSyncReport(string Kind, string Repository, bool DryRun, IReadOnlyList<string> Configured,
    IReadOnlyList<string> Remote, IReadOnlyList<string> Missing, IReadOnlyList<string> Stale,
    IReadOnlyList<string> Unmanaged, IReadOnlyList<string> Created, IReadOnlyList<string> Updated);

public interface IGitHubLabelCatalog
{
    Task<GitHubLabelSyncReport> SynchronizeAsync(string repository, IReadOnlyList<ConfiguredGitHubLabel> configured, bool apply,
        CancellationToken cancellationToken = default);
}

/// <summary>Synchronizes configured label metadata through bounded GitHub API operations.</summary>
public sealed class GitHubLabelCatalog(HttpClient http, IGitHubCredentialProvider credentials, IGitHubWriteClient writer) : IGitHubLabelCatalog
{
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex LabelPattern = new("^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant);

    public async Task<GitHubLabelSyncReport> SynchronizeAsync(string repository, IReadOnlyList<ConfiguredGitHubLabel> configured, bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (!RepositoryPattern.IsMatch(repository)) throw new ArgumentException("A canonical GitHub repository is required.", nameof(repository));
        if (configured.Count is < 1 or > 200 || configured.Any(label => !LabelPattern.IsMatch(label.Name) ||
            label.Description.Length > 1000 || !Regex.IsMatch(label.Color, "^[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)) ||
            configured.Select(label => label.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Count)
            throw new ArgumentException("Configured labels are invalid or duplicated.", nameof(configured));
        var remote = await ReadAsync(repository, cancellationToken);
        var configuredNames = configured.Select(label => label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = configured.Where(label => !remote.ContainsKey(label.Name)).ToArray();
        var stale = configured.Where(label => remote.TryGetValue(label.Name, out var current) &&
            (current.Description != label.Description || !string.Equals(current.Color, label.Color, StringComparison.OrdinalIgnoreCase))).ToArray();
        var unmanaged = remote.Keys.Where(name => !configuredNames.Contains(name)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var created = new List<string>();
        var updated = new List<string>();
        if (apply)
        {
            foreach (var label in missing)
            {
                using var response = await SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{repository}/labels"), label, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub rejected configured label creation: {label.Name}.");
                created.Add(label.Name);
            }
            foreach (var label in stale)
            {
                using var response = await SendAsync(HttpMethod.Patch,
                    new Uri($"https://api.github.com/repos/{repository}/labels/{Uri.EscapeDataString(label.Name)}"), label, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub rejected configured label update: {label.Name}.");
                updated.Add(label.Name);
            }
        }
        return new("github-labels", repository, !apply, configured.Select(label => label.Name).ToArray(),
            remote.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            apply ? [] : missing.Select(label => label.Name).ToArray(),
            apply ? [] : stale.Select(label => label.Name).ToArray(), unmanaged, created, updated);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, ConfiguredGitHubLabel label, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { name = label.Name, description = label.Description, color = label.Color }), Encoding.UTF8, "application/json");
        return await writer.SendAsync(method, endpoint, content, cancellationToken);
    }

    private async Task<Dictionary<string, ConfiguredGitHubLabel>> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        var token = await credentials.GetTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("GitHub authentication is unavailable.");
        var labels = new Dictionary<string, ConfiguredGitHubLabel>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri($"https://api.github.com/repos/{repository}/labels?per_page=100&page={page}"));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("sdeveng");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub label listing failed.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub label listing must be an array.");
            var items = document.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                var name = item.GetProperty("name").GetString() ?? throw new JsonException("Label name is missing.");
                var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
                var color = item.GetProperty("color").GetString() ?? throw new JsonException("Label color is missing.");
                if (!LabelPattern.IsMatch(name) || !labels.TryAdd(name, new(name, description, color)))
                    throw new JsonException("Remote label identity is invalid or duplicated.");
            }
            if (items.Length < 100) return labels;
        }
        throw new InvalidDataException("GitHub label listing exceeded the 1,000-label bound.");
    }
}
