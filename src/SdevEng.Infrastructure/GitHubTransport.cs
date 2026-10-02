using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

/// <summary>Performs read-only requests against the GitHub API.</summary>
public interface IGitHubReadClient
{
    Task<HttpResponseMessage> GetAsync(Uri endpoint, CancellationToken cancellationToken = default);
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

/// <summary>Deterministic evidence recorded by release-validation execution.</summary>
