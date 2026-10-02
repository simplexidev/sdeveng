using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdevEng;

public sealed class JevHttpSession : IDisposable
{
    private readonly HttpClientHandler handler = new() { AllowAutoRedirect = false };
    private readonly HttpClient http;
    public JevClient Client { get; }

    public JevHttpSession(JevSettings settings, string cachePath)
    {
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        Client = new JevClient(http, settings, cachePath);
    }

    public void Dispose()
    {
        http.Dispose();
        handler.Dispose();
    }
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
