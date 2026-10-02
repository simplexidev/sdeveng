using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SdevEng;

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
            parts[5] == "labels" && method == HttpMethod.Post ||
            parts.Length == 7 && parts[0] == "repos" && parts[1].Length > 0 && parts[2].Length > 0 &&
            parts[3] == "issues" && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var deleteIssueNumber) && deleteIssueNumber > 0 &&
            parts[5] == "labels" && parts[6] == Uri.EscapeDataString("READY") && method == HttpMethod.Delete;
        allowed |= parts.Length == 7 && parts[0] == "repos" && parts[1].Length > 0 && parts[2].Length > 0 &&
            parts[3] == "actions" && parts[4] == "jobs" && Regex.IsMatch(parts[5], @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant) &&
            parts[6] == "rerun" && method == HttpMethod.Post;
        allowed |= parts.Length == 7 && parts[0] == "repos" && parts[1].Length > 0 && parts[2].Length > 0 &&
            parts[3] == "actions" && parts[4] == "runs" && Regex.IsMatch(parts[5], @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant) &&
            parts[6] == "rerun-failed-jobs" && method == HttpMethod.Post;
        allowed |= parts.Length is 4 or 5 && parts[0] == "repos" && parts[1].Length > 0 && parts[2].Length > 0 && parts[3] == "labels" &&
            (method == HttpMethod.Post && parts.Length == 4 || method == HttpMethod.Patch && parts.Length == 5 &&
             Regex.IsMatch(Uri.UnescapeDataString(parts[4]), @"^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant));
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

/// <summary>Typed single-job GitHub Actions rerun mutation.</summary>
public sealed class GitHubActionsJobRerunWriter(IGitHubWriteClient client)
{
    public Task<HttpResponseMessage> RerunAsync(string repository, string jobId, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(repository ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(jobId ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Rerun requires an exact repository and provider job ID.");
        return client.SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{repository}/actions/jobs/{jobId}/rerun"), cancellationToken: cancellationToken);
    }
}

public sealed class GitHubActionsFailedJobsRerunWriter(IGitHubWriteClient client)
{
    public Task<HttpResponseMessage> RerunAsync(string repository, string runId, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(repository ?? "", @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(runId ?? "", @"^[1-9][0-9]{0,31}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Rerun requires an exact repository and provider run ID.");
        return client.SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{repository}/actions/runs/{runId}/rerun-failed-jobs"), cancellationToken: cancellationToken);
    }
}
