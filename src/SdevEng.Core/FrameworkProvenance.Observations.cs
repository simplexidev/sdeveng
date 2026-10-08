using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SdevEng;

public sealed record FrameworkObservation(string FrameworkId, string SourceUri, string? ObservedRevision,
    string? ObservedHash, int? ObservedMajorVersion, string CapturedAt, string Provenance, string? UnavailableReason);
public sealed record FrameworkObservations(int SchemaVersion, FrameworkObservation[] Observations);
public sealed record FrameworkDriftEntry(string FrameworkId, string SourceUri, string CurrentRevision,
    string CurrentHash, JsonNode SupportedMajorVersions, FrameworkObservation? Observed, string Classification, string[] Reasons);
public sealed record FrameworkDriftPlan(int SchemaVersion, string Kind, string Mode, string AutomaticAction, FrameworkDriftEntry[] Entries);

public static partial class FrameworkProvenance
{
    public const int MaxObservationBytes = 16384;
    static readonly JsonSerializerOptions ObservationJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static FrameworkObservations ReadObservations(string json, JsonNode document)
    {
        var manifest = Read(document);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxObservationBytes) throw new FormatException("Observations exceed 16384 bytes.");
        FrameworkObservations input;
        try { input = JsonSerializer.Deserialize<FrameworkObservations>(json, ObservationJson) ?? throw new FormatException("Empty observations."); }
        catch (JsonException error) { throw new FormatException("Malformed observations.", error); }
        if (input.SchemaVersion != 1 || input.Observations is null || input.Observations.Length > FrameworkIds.Count)
            throw new FormatException("Invalid observation version or count.");
        var entries = manifest["frameworks"]?.AsArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in input.Observations)
        {
            if (observation is null || !FrameworkIds.Contains(observation.FrameworkId) || !seen.Add(observation.FrameworkId))
                throw new FormatException("Unknown or duplicate observation ID.");
            var entry = entries?.SingleOrDefault(e => e!["frameworkId"]!.GetValue<string>() == observation.FrameworkId);
            // Historical v2 has only the dotnet snapshot authority; never invent generic pins.
            var source = entry?["sourceUri"]?.GetValue<string>() ?? (observation.FrameworkId == "dotnet" ? manifest["snapshot"]!["url"]?.GetValue<string>() : null);
            if (source is null || observation.SourceUri != source) throw new FormatException("Observation source does not match pinned authority.");
            if (observation.ObservedRevision is { } revision && (revision == "unknown" || !Regex.IsMatch(revision, "^[A-Za-z0-9][A-Za-z0-9._/+:-]{0,199}$"))) throw new FormatException("Invalid observed revision.");
            if (observation.ObservedHash is { } hash && !Regex.IsMatch(hash, "^sha256:[0-9a-f]{64}$")) throw new FormatException("Invalid observed hash.");
            if (observation.ObservedMajorVersion is <= 0) throw new FormatException("Invalid observed major.");
            if (observation.CapturedAt is null || !Regex.IsMatch(observation.CapturedAt, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(observation.CapturedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new FormatException("Invalid observation capture time.");
            if (string.IsNullOrWhiteSpace(observation.Provenance) || observation.Provenance.Length > 300 || observation.UnavailableReason is { Length: > 300 } || observation.UnavailableReason is not null && string.IsNullOrWhiteSpace(observation.UnavailableReason)) throw new FormatException("Invalid observation provenance or unavailable reason.");
            if ((observation.ObservedRevision is null || observation.ObservedHash is null || observation.ObservedMajorVersion is null) && observation.UnavailableReason is null) throw new FormatException("Unknown observation facts require an unavailable reason.");
        }
        return input;
    }

    public static FrameworkDriftPlan Plan(JsonNode document, string? observationsJson, bool refresh)
    {
        var manifest = Read(document);
        var observations = observationsJson is null ? [] : ReadObservations(observationsJson, manifest).Observations;
        var entries = new List<FrameworkDriftEntry>();
        foreach (var id in FrameworkIds)
        {
            var baseline = manifest["frameworks"]?.AsArray().SingleOrDefault(e => e!["frameworkId"]!.GetValue<string>() == id);
            var source = baseline?["sourceUri"]?.GetValue<string>() ?? (id == "dotnet" ? manifest["snapshot"]!["url"]!.GetValue<string>() : "unknown");
            var revision = baseline?["sourceRevision"]?.GetValue<string>() ?? (id == "dotnet" ? manifest["snapshot"]!["commit"]!.GetValue<string>() : "unknown");
            var hash = baseline?["sourceHash"]?.GetValue<string>() ?? "unknown";
            var majors = baseline?["supportedMajorVersions"]?.DeepClone() ?? JsonValue.Create("unknown")!;
            var observed = observations.SingleOrDefault(o => o.FrameworkId == id);
            var reasons = new List<string>();
            var changed = false; var unsupported = false;
            if (observed is null) reasons.Add("observations-unavailable");
            else
            {
                Compare(revision, observed.ObservedRevision, "revision");
                Compare(hash, observed.ObservedHash, "hash");
                if (majors is JsonArray supported && observed.ObservedMajorVersion is int major)
                {
                    unsupported = !supported.Any(v => v!.GetValue<int>() == major);
                    if (unsupported) reasons.Add("unsupported-major");
                }
                else reasons.Add("major-unknown");
                if (observed.UnavailableReason is not null) reasons.Add("observation-unavailable: " + observed.UnavailableReason);
            }
            var classification = unsupported ? "unsupported" : changed ? "review-required" : reasons.Count > 0 ? "unknown" : "current";
            entries.Add(new(id, source, revision, hash, majors, observed, classification, reasons.ToArray()));
            void Compare(string current, string? value, string fact)
            {
                if (current == "unknown" || value is null) reasons.Add(fact + "-unknown");
                else if (current != value) { changed = true; reasons.Add(fact + "-changed"); }
            }
        }
        var kind = "framework-provenance-drift";
        return new(1, kind, refresh ? "plan" : "facts", "none", entries.ToArray());
    }
}
