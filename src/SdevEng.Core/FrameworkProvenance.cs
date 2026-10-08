using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SdevEng;

/// <summary>Compatibility reader for the existing provenance inventory; never refreshes or writes pins.</summary>
public static class FrameworkProvenance
{
    public static readonly IReadOnlyList<string> FrameworkIds = Array.AsReadOnly(new[]
        { "dotnet", "microsoft.extensions", "system.commandline", "avalonia", "terminal.gui" });

    public static JsonObject Read(JsonNode document)
    {
        try
        {
            var manifest = document.AsObject();
            var version = manifest["manifestVersion"]?.GetValue<int>();
            if (version is not (2 or 3)) throw new FormatException("Unsupported provenance manifest version.");
            var snapshot = manifest["snapshot"]!.AsObject();
            Text(snapshot, "repository");
            if (!Regex.IsMatch(Text(snapshot, "commit"), "^[0-9a-f]{40}$")) throw new FormatException("Invalid provenance commit.");
            foreach (var decision in manifest["decisions"]!.AsArray())
                Strings(decision!.AsObject(), "upstreamPaths", true);
            if (version == 2)
            {
                if (manifest.ContainsKey("frameworks")) throw new FormatException("Version 2 cannot contain framework entries.");
            }
            else
            {
                var entries = manifest["frameworks"]!.AsArray();
                if (entries.Count == 0) throw new FormatException("Framework entries are required.");
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in entries)
                {
                    var id = Text(node!.AsObject(), "frameworkId");
                    if (!FrameworkIds.Contains(id) || !identities.Add(id)) throw new FormatException("Duplicate or unknown framework ID.");
                }
                foreach (var node in entries)
                {
                    var entry = node!.AsObject();
                    string[] fields = ["frameworkId", "aliases", "supportedMajorVersions", "sourceUri", "releaseUri", "documentationUri", "sourceRevision", "sourceHash", "capturedAt", "consumingSkillIds"];
                    if (entry.Count != fields.Length || fields.Any(f => !entry.ContainsKey(f))) throw new FormatException("Invalid framework entry fields.");
                    var id = Text(entry, "frameworkId");
                    foreach (var alias in Strings(entry, "aliases", false))
                        if (FrameworkIds.Contains(alias, StringComparer.OrdinalIgnoreCase) || !identities.Add(alias)) throw new FormatException("Ambiguous framework alias.");
                    var majors = entry["supportedMajorVersions"]!;
                    if (majors is JsonArray versions)
                    {
                        var values = versions.Select(v => v!.GetValue<int>()).ToArray();
                        if (values.Length == 0 || values.Any(v => v < 1) || values.Distinct().Count() != values.Length) throw new FormatException("Invalid supported major versions.");
                    }
                    else if (majors.GetValue<string>() != "unknown") throw new FormatException("Invalid unknown version marker.");
                    if (!Uri.TryCreate(Text(entry, "sourceUri"), UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath == "/") throw new FormatException("Invalid pinned source URI.");
                    foreach (var field in new[] { "releaseUri", "documentationUri" })
                        if (!Uri.TryCreate(Text(entry, field), UriKind.Absolute, out var reference) || reference.Scheme != "https" || string.IsNullOrEmpty(reference.Host)) throw new FormatException($"Invalid authoritative {field}.");
                    Text(entry, "sourceRevision");
                    if (!Regex.IsMatch(Text(entry, "sourceHash"), "^(unknown|sha256:[0-9a-f]{64})$")) throw new FormatException("Invalid source hash.");
                    var captured = Text(entry, "capturedAt");
                    if (captured != "unknown" && (!Regex.IsMatch(captured, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(captured, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw new FormatException("Invalid capture time.");
                    Strings(entry, "consumingSkillIds", true);
                }
            }
            return (JsonObject)manifest.DeepClone();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            throw new FormatException("Malformed framework provenance.", error);
        }
    }

    static string Text(JsonObject node, string field) => node[field]?.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text)
        ? text : throw new FormatException($"Missing provenance {field}.");

    static string[] Strings(JsonObject node, string field, bool required)
    {
        var values = node[field]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        if ((required && values.Length == 0) || values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new FormatException($"Invalid provenance {field}.");
        return values;
    }
}
