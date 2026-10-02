using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdevEng;

public sealed record FinalPrEvidencePolicy(IReadOnlyList<string> RequiredChecks, VerificationEvidencePolicy Sources);

public static class VerificationEvidenceFiles
{
    private static readonly Regex Sha = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);

    public static FinalPrEvidencePolicy ReadPolicy(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported verification policy version.");
        var final = root.GetProperty("finalPr");
        var checks = final.GetProperty("requiredChecks").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        if (checks.Length == 0 || checks.Any(string.IsNullOrWhiteSpace) || checks.Distinct(StringComparer.Ordinal).Count() != checks.Length)
            throw new InvalidDataException("Final PR evidence policy requires unique named checks.");
        return new(checks, new(final.GetProperty("requireLocal").GetBoolean(), final.GetProperty("requireHosted").GetBoolean()));
    }

    public static IReadOnlyList<VerificationResult> ReadHosted(string path, string commitSha)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("validationTier").GetString() != "final-pr")
            throw new InvalidDataException("Hosted evidence must be final PR evidence version 2.");
        return ReadChecks(root, commitSha, "hosted");
    }

    public static IReadOnlyList<VerificationResult> ReadLocal(string path, string commitSha)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported local verification bundle version.");
        return ReadChecks(root, commitSha, "local");
    }

    private static IReadOnlyList<VerificationResult> ReadChecks(JsonElement root, string commitSha, string source)
    {
        if (!Sha.IsMatch(commitSha) || root.GetProperty("commitSha").GetString() != commitSha)
            throw new InvalidDataException("Verification evidence does not match the exact requested commit.");
        var results = new List<VerificationResult>();
        foreach (var check in root.GetProperty("checks").EnumerateArray())
        {
            var status = check.GetProperty("status").GetString() ?? "";
            var exitCode = check.GetProperty("exitCode").GetInt32();
            var name = check.GetProperty("check").GetString() ?? "";
            var artifact = check.GetProperty("artifact").GetString() ?? "";
            var environment = check.GetProperty("environmentIdentity").GetString() ?? "";
            var version = check.GetProperty("schemaVersion").GetInt32();
            if (version is not (1 or 2) || check.GetProperty("source").GetString() != source ||
                name.Length is < 1 or > 512 || string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(environment) ||
                status is not ("passed" or "failed" or "cancelled" or "timed-out") ||
                version == 1 && status is ("cancelled" or "timed-out") || exitCode < 0 || (exitCode == 0) != (status == "passed"))
                throw new InvalidDataException("Verification result is invalid or lacks environment identity.");
            results.Add(new(version, source, name, status, exitCode, artifact) { EnvironmentIdentity = environment });
        }
        if (results.GroupBy(result => result.Check, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Verification evidence contains duplicate checks.");
        return results;
    }
}
