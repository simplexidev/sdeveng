using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SdevEng;

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
        return node.ToJsonString();
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
    public static string? Read() => Environment.GetEnvironmentVariable(EnvironmentVariable);
    public static bool IsConfigured(Func<string?> source) => !string.IsNullOrEmpty(source());
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
    public static bool Authorize(HttpRequestMessage request, Func<string?> source)
    {
        var key = source();
        if (string.IsNullOrEmpty(key)) return false;
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return true; }
        catch (FormatException) { return false; }
    }
}
