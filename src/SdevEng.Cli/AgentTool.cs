using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SdevEng;

public sealed record ReleaseValidationArtifact(string Reference, string Sha256);
public sealed record ReleaseValidationGate(string GateId, string Status, string? Reason = null, IReadOnlyList<ReleaseValidationArtifact>? Artifacts = null);
public sealed record ReleaseValidationManifest(int SchemaVersion, string Repository, string CommitSha, string Profile, IReadOnlyList<ReleaseValidationGate> Gates)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Tag { get; init; }
}

/// <summary>Creates, validates, and serializes the versioned release-validation evidence contract.</summary>
public static class ReleaseValidationEvidence
{
    public const int SchemaVersion = 1;
    static readonly Regex GatePattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    static readonly Regex ShaPattern = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    static readonly Regex RepoPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    public static ReleaseValidationManifest Create(string repository, string commitSha, string profile, IEnumerable<ReleaseValidationGate> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var ordered = gates.Select(g => g with { Artifacts = (g.Artifacts ?? []).OrderBy(a => a.Reference, StringComparer.Ordinal).ToArray() })
            .OrderBy(g => g.GateId, StringComparer.Ordinal).ToArray();
        var manifest = new ReleaseValidationManifest(SchemaVersion, repository, commitSha, profile, ordered);
        Validate(manifest);
        return manifest;
    }

    public static void Validate(ReleaseValidationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != SchemaVersion || !RepoPattern.IsMatch(manifest.Repository ?? "") || !ShaPattern.IsMatch(manifest.CommitSha ?? "") ||
            string.IsNullOrWhiteSpace(manifest.Profile) || manifest.Profile.Length > 64 || !Regex.IsMatch(manifest.Profile, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            manifest.Tag is not null && !Regex.IsMatch(manifest.Tag, @"^v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Release validation manifest identity is invalid.", nameof(manifest));
        if (manifest.Gates is null || manifest.Gates.Count > 200) throw new ArgumentException("Release validation gates are invalid or exceed 200 records.", nameof(manifest));
        var gateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in manifest.Gates)
        {
            if (gate is null || !GatePattern.IsMatch(gate.GateId ?? "") || !gateIds.Add(gate.GateId!) || gate.Status is not ("passed" or "failed" or "unsupported" or "not-observed"))
                throw new ArgumentException("Release validation gate identity or status is invalid or duplicated.", nameof(manifest));
            if (gate.Status == "passed" ? gate.Reason is not null : string.IsNullOrWhiteSpace(gate.Reason) || gate.Reason.Length > 512)
                throw new ArgumentException("Non-passed gates require a bounded reason; passed gates must not have one.", nameof(manifest));
            if (gate.Artifacts is null || gate.Artifacts.Count > 100) throw new ArgumentException("Release validation artifacts are invalid or exceed 100 records per gate.", nameof(manifest));
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in gate.Artifacts)
                if (artifact is null || !SafeArtifactReference(artifact.Reference) || !ShaPattern.IsMatch(artifact.Sha256 ?? "") || !refs.Add(artifact.Reference))
                    throw new ArgumentException("Release validation artifact reference or SHA-256 is invalid or duplicated.", nameof(manifest));
        }
    }

    public static byte[] Serialize(ReleaseValidationManifest manifest)
    {
        Validate(manifest);
        if (!manifest.Gates.SequenceEqual(manifest.Gates.OrderBy(g => g.GateId, StringComparer.Ordinal)) || manifest.Gates.Any(g => !g.Artifacts!.SequenceEqual(g.Artifacts!.OrderBy(a => a.Reference, StringComparer.Ordinal))))
            throw new ArgumentException("Manifest gates and artifact references must be in canonical ordinal order.", nameof(manifest));
        return JsonSerializer.SerializeToUtf8Bytes(manifest, AgentTool.Json);
    }

    static bool SafeArtifactReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.StartsWith('/') || value.Contains('\\') || Regex.IsMatch(value, "^[A-Za-z]:") || value.Any(char.IsControl)) return false;
        if (value.StartsWith("logical:", StringComparison.Ordinal)) return Regex.IsMatch(value[8..], "^[A-Za-z0-9][A-Za-z0-9._:-]{0,199}$", RegexOptions.CultureInvariant);
        return value.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && Regex.IsMatch(part, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant));
    }
}

/// <summary>Publishes one locally validated release-validation manifest for the current repository revision.</summary>
public static class ReleaseValidationPublisher
{
    public static async Task<(ReleaseValidationManifest Manifest, string Output)> PublishAsync(string root, string profile, string output, CancellationToken cancellationToken = default, string? evidenceFile = null, string? expectedTag = null)
    {
        var fullRoot = Path.GetFullPath(root);
        if (Path.IsPathRooted(output) || output.Contains('\\') || output.Any(char.IsControl) || output.Split('/').Any(part => part is ".." or "." or "" || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Output must be a repository-relative path without dot segments.", nameof(output));
        var fullOutput = Path.GetFullPath(Path.Combine(fullRoot, output));
        if (!fullOutput.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Output must remain inside the repository.", nameof(output));
        SafeFiles.NoLinks(fullOutput);
        var remote = await Git.RemoteUrl(fullRoot, "origin");
        var repository = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(remote);
        var commit = await Git.ResolveCommit(fullRoot, "HEAD");
        var gates = new[]
        {
            new ReleaseValidationGate("build", "not-observed", "trusted build evidence was not supplied"),
            new ReleaseValidationGate("format", "not-observed", "trusted format evidence was not supplied"),
            new ReleaseValidationGate("reproducibility", "unsupported", "no repository-owned deterministic reproducibility check is available"),
            new ReleaseValidationGate("security-audit", "not-observed", "trusted security audit evidence was not supplied"),
            new ReleaseValidationGate("tests", "not-observed", "trusted test evidence was not supplied")
        };
        ReleaseValidationManifest manifest;
        if (evidenceFile is null)
        {
            manifest = ReleaseValidationEvidence.Create(repository.Owner + "/" + repository.Repository, commit, profile, gates);
        }
        else
        {
            if (Path.IsPathRooted(evidenceFile) || evidenceFile.Contains('\\') || evidenceFile.Split('/').Any(part => part is ".." or "." or "" || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Evidence file must be repository-relative.", nameof(evidenceFile));
            var fullEvidence = Path.GetFullPath(Path.Combine(fullRoot, evidenceFile));
            SafeFiles.NoLinks(fullEvidence);
            var supplied = JsonSerializer.Deserialize<ReleaseValidationManifest>(await File.ReadAllTextAsync(fullEvidence, cancellationToken), AgentTool.Json)
                ?? throw new ArgumentException("Release evidence is empty.", nameof(evidenceFile));
            ReleaseValidationEvidence.Validate(supplied);
            if (supplied.Repository != repository.Owner + "/" + repository.Repository || supplied.CommitSha != commit ||
                supplied.Profile != profile || string.IsNullOrWhiteSpace(expectedTag) || supplied.Tag != expectedTag)
                throw new ArgumentException("Release evidence does not match the exact repository, commit, profile and tag.", nameof(evidenceFile));
            manifest = ReleaseValidationEvidence.Create(supplied.Repository, supplied.CommitSha, supplied.Profile, supplied.Gates) with { Tag = supplied.Tag };
        }
        var bytes = ReleaseValidationEvidence.Serialize(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        SafeFiles.Atomic(fullOutput, Encoding.UTF8.GetString(bytes));
        return (manifest, fullOutput);
    }
}

public sealed record IssueFileReference(string Path, int? StartLine, int? EndLine, bool Exists);
public sealed record IssueSymbolReference(string Value);
public sealed record IssueReferenceFacts(IssueFileReference[] Files, IssueSymbolReference[] Symbols);
public sealed record LinkedIssueReference(string Owner, string Repository, int Number, string Kind);
public sealed record TriageFacts(string? CandidateRepository, IReadOnlyList<LinkedIssueReference> References, bool ReferencesTruncated);
public sealed record IssueTriageFacts(
    string? CandidateRepository, string Title, string Body,
    IReadOnlyList<IssueFileReference> Files, IReadOnlyList<IssueSymbolReference> Symbols,
    IReadOnlyList<LinkedIssueReference> LinkedReferences, IReadOnlyList<string> AreaHints,
    IReadOnlyList<string> UnresolvedFamilies, bool Truncated);

public sealed record TriageLabelCandidate(string Family, string Label, double Confidence, IReadOnlyList<string> Evidence);
public sealed record TriageClassification(IReadOnlyList<TriageLabelCandidate> Candidates, IReadOnlyList<string> UnresolvedFamilies);
public sealed record TriageSelectedLabel(string Label, double Confidence, IReadOnlyList<string> Evidence);
public sealed record TriageSelectedFamily(string Family, IReadOnlyList<TriageSelectedLabel> Labels);
public sealed record TriageDecision(IReadOnlyList<TriageSelectedFamily> Selected, IReadOnlyList<string> UnresolvedFamilies, bool NeedsHumanReview);

public interface ITriageSemanticClassifier
{
    IReadOnlyList<TriageLabelCandidate> Classify(IssueTriageFacts facts, IReadOnlyList<TriageLabelCandidate> deterministicCandidates, JsonElement labelCatalog);
}

/// <summary>Safe semantic-classifier default that makes no model or network calls.</summary>
public sealed class AbstainingTriageSemanticClassifier : ITriageSemanticClassifier
{
    public IReadOnlyList<TriageLabelCandidate> Classify(IssueTriageFacts facts, IReadOnlyList<TriageLabelCandidate> deterministicCandidates, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(deterministicCandidates);
        return Array.Empty<TriageLabelCandidate>();
    }
}

/// <summary>Classifies only explicit issue evidence against the configured label catalog.</summary>
public static class TriageClassifier
{
    public const double MinimumAutomaticConfidence = 0.80;
    static readonly Regex TypeToken = new(@"(?<![A-Za-z0-9_-])type:([A-Za-z0-9_-]+)(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex ExplicitTriageToken = new(@"(?<![A-Za-z0-9_-])(?<family>risk|complexity):(?<value>[A-Za-z0-9_-]+)(?![A-Za-z0-9_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<TriageLabelCandidate> BoundSemanticCandidates(IEnumerable<TriageLabelCandidate> candidates, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = labels.EnumerateArray().Select(item => (Family: item.GetProperty("family").GetString(), Name: item.GetProperty("name").GetString()))
            .ToHashSet();
        return candidates.Where(candidate => configured.Contains((candidate.Family, candidate.Label)) &&
                candidate.Family.Length is > 0 and <= 64 && candidate.Label.Length is > 0 and <= 100 &&
                double.IsFinite(candidate.Confidence) && candidate.Confidence is >= 0 and <= 1)
            .Take(20)
            .Select(candidate => candidate with { Evidence = candidate.Evidence.Take(10).Select(value => value.Length <= 300 ? value : value[..300]).ToArray() })
            .ToArray();
    }

    public static TriageDecision ClassifyDecision(IssueTriageFacts facts, JsonElement labelCatalog, ITriageSemanticClassifier semanticClassifier)
    {
        ArgumentNullException.ThrowIfNull(semanticClassifier);
        var deterministic = Classify(facts, labelCatalog);
        var semantic = semanticClassifier.Classify(facts, deterministic.Candidates, labelCatalog);
        return Combine(deterministic.Candidates, semantic, deterministic.UnresolvedFamilies, labelCatalog);
    }

    public static TriageDecision Combine(IEnumerable<TriageLabelCandidate> deterministicCandidates,
        IEnumerable<TriageLabelCandidate> semanticCandidates, IEnumerable<string> unresolvedFamilies, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(deterministicCandidates);
        ArgumentNullException.ThrowIfNull(semanticCandidates);
        ArgumentNullException.ThrowIfNull(unresolvedFamilies);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = labels.EnumerateArray().Select(item => (Name: item.GetProperty("name").GetString(), Family: item.GetProperty("family").GetString()))
            .Where(item => item.Name is not null && item.Family is not null)
            .ToHashSet();
        var configuredFamilies = configured.Select(item => item.Family!).ToHashSet(StringComparer.Ordinal);
        var exclusiveFamilies = new HashSet<string>(["type", "risk", "complexity", "scope"], StringComparer.Ordinal);
        var candidates = new List<TriageLabelCandidate>();
        var unresolved = new HashSet<string>(unresolvedFamilies.Where(configuredFamilies.Contains), StringComparer.Ordinal);
        var invalidCandidate = false;
        foreach (var (candidate, semantic) in deterministicCandidates.Select(x => (x, false)).Concat(semanticCandidates.Select(x => (x, true))))
        {
            if (candidate is null || !configured.Contains((candidate.Label, candidate.Family)) ||
                !double.IsFinite(candidate.Confidence) || candidate.Confidence is < 0 or > 1 || candidate.Evidence is null)
            {
                invalidCandidate = true;
                if (candidate?.Family is { } invalidFamily && configuredFamilies.Contains(invalidFamily)) unresolved.Add(invalidFamily);
                continue;
            }
            if (semantic && candidate.Confidence < MinimumAutomaticConfidence)
            {
                unresolved.Add(candidate.Family);
                continue;
            }
            candidates.Add(semantic ? candidate : candidate with { Confidence = 1.0 });
        }

        var selected = new List<TriageSelectedFamily>();
        foreach (var family in configuredFamilies.Order(StringComparer.Ordinal))
        {
            var familyCandidates = candidates.Where(candidate => candidate.Family == family)
                .GroupBy(candidate => candidate.Label, StringComparer.Ordinal)
                .Select(group => new TriageLabelCandidate(family, group.Key, group.Max(candidate => candidate.Confidence),
                    group.SelectMany(candidate => candidate.Evidence).Distinct(StringComparer.Ordinal).Take(10).Select(evidence => evidence.Length <= 300 ? evidence : evidence[..300]).ToArray()))
                .OrderBy(candidate => candidate.Label, StringComparer.Ordinal).ToArray();
            if (familyCandidates.Length == 0) { unresolved.Add(family); continue; }
            if (exclusiveFamilies.Contains(family) && familyCandidates.Length > 1) { unresolved.Add(family); continue; }
            selected.Add(new(family, familyCandidates.Select(candidate => new TriageSelectedLabel(candidate.Label, candidate.Confidence, candidate.Evidence)).ToArray()));
            unresolved.Remove(family);
        }
        var unresolvedResult = unresolved.Order(StringComparer.Ordinal).ToArray();
        return new(selected.OrderBy(item => item.Family, StringComparer.Ordinal).ToArray(), unresolvedResult,
            invalidCandidate || unresolvedResult.Length > 0);
    }

    public static TriageClassification Classify(IssueTriageFacts facts, JsonElement labelCatalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!labelCatalog.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");

        var configured = labels.EnumerateArray().Select(item =>
        {
            var name = item.GetProperty("name").GetString() ?? throw new JsonException("Configured label name is missing.");
            var family = item.GetProperty("family").GetString() ?? throw new JsonException("Configured label family is missing.");
            return (Name: name, Family: family);
        }).ToArray();
        var candidates = new List<TriageLabelCandidate>();
        var unresolved = facts.UnresolvedFamilies.Distinct(StringComparer.Ordinal).ToList();

        var typeLabels = configured.Where(x => x.Family == "type" && x.Name.StartsWith("type:", StringComparison.OrdinalIgnoreCase)).ToArray();
        var typeEvidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in typeLabels)
        {
            var suffix = label.Name["type:".Length..];
            var prefix = Regex.IsMatch(facts.Title, @"^\s*" + Regex.Escape(suffix) + @"\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var token = TypeToken.Matches(facts.Title + "\n" + facts.Body).Cast<Match>()
                .Any(match => string.Equals(match.Groups[1].Value, suffix, StringComparison.OrdinalIgnoreCase));
            if (prefix || token)
            {
                var evidence = new List<string>();
                if (prefix) evidence.Add("title-prefix:" + suffix + ":");
                if (token) evidence.Add("type-token:" + suffix);
                typeEvidence[label.Name] = evidence;
            }
        }
        if (typeEvidence.Count == 1)
        {
            var pair = typeEvidence.Single();
            candidates.Add(new("type", pair.Key, 1.0, pair.Value));
            unresolved.RemoveAll(x => x == "type");
        }

        var areaLabels = configured.Where(x => x.Family == "area" && x.Name.StartsWith("area:", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var label in areaLabels)
        {
            var suffix = label.Name["area:".Length..];
            var evidence = facts.AreaHints.Where(hint =>
                string.Equals(hint, suffix, StringComparison.OrdinalIgnoreCase) ||
                (hint.StartsWith("area:", StringComparison.OrdinalIgnoreCase) && string.Equals(hint["area:".Length..], suffix, StringComparison.OrdinalIgnoreCase)))
                .Select(hint => "area-hint:" + hint).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (evidence.Length > 0) candidates.Add(new("area", label.Name, 1.0, evidence));
        }
        if (candidates.Any(x => x.Family == "area")) unresolved.RemoveAll(x => x == "area");

        var declaredTokens = ExplicitTriageToken.Matches(facts.Title + "\n" + facts.Body).Cast<Match>().ToArray();
        foreach (var family in new[] { "risk", "complexity" })
        {
            var familyLabels = configured.Where(label => label.Family == family && label.Name.StartsWith(family + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
            var matchingLabels = declaredTokens
                .Where(match => string.Equals(match.Groups["family"].Value, family, StringComparison.OrdinalIgnoreCase))
                .Select(match => familyLabels.FirstOrDefault(label => string.Equals(label.Name[(family.Length + 1)..], match.Groups["value"].Value, StringComparison.OrdinalIgnoreCase)))
                .Where(label => label.Name is not null)
                .DistinctBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matchingLabels.Length == 1)
            {
                var label = matchingLabels[0];
                var evidence = declaredTokens
                    .Where(match => string.Equals(match.Groups["family"].Value, family, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(label.Name[(family.Length + 1)..], match.Groups["value"].Value, StringComparison.OrdinalIgnoreCase))
                    .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                candidates.Add(new(family, label.Name, 1.0, evidence));
                unresolved.RemoveAll(x => x == family);
            }
        }

        var candidateRepository = facts.CandidateRepository;
        if (!string.IsNullOrWhiteSpace(candidateRepository))
        {
            var crossRepository = facts.LinkedReferences.Any(reference =>
                !string.Equals(candidateRepository, reference.Owner + "/" + reference.Repository, StringComparison.OrdinalIgnoreCase));
            var scopeLabel = crossRepository ? "scope:cross-repo" : "scope:single-repo";
            if (configured.Any(label => label.Family == "scope" && string.Equals(label.Name, scopeLabel, StringComparison.OrdinalIgnoreCase)))
            {
                var evidence = crossRepository ? "linked-repository:external" : "linked-repository:local-or-none";
                candidates.Add(new("scope", scopeLabel, 1.0, [evidence]));
                unresolved.RemoveAll(x => x == "scope");
            }
        }
        return new(candidates, unresolved);
    }
}

/// <summary>Extracts explicit issue references and the invocation repository from normalized issue text.</summary>
public static class TriageFactsExtractor
{
    static readonly Regex HttpsReference = new(@"https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/(issues|pull)/([1-9][0-9]*)(?![A-Za-z0-9_/?#.-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex QualifiedReference = new(@"(?<![A-Za-z0-9_./-])([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)#([1-9][0-9]*)(?![0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex LocalReference = new(@"(?<![A-Za-z0-9_./-])#([1-9][0-9]*)(?![0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex AreaHint = new(@"(?<![A-Za-z0-9_-])area:([A-Za-z0-9_.-]+)(?![A-Za-z0-9_.-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static async Task<IssueTriageFacts> ExtractIssueAsync(GitHubIssue issue, string repositoryRoot, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        var text = issue.Title + "\n" + (issue.Body ?? "");
        var references = await ExtractAsync(text, repositoryRoot, maxItems, cancellationToken);
        var sourceFacts = IssueReferenceExtractor.Extract(issue, repositoryRoot, maxItems);
        var files = sourceFacts.Files.Take(maxItems).ToArray();
        var symbols = sourceFacts.Symbols.Take(maxItems).ToArray();
        var areas = new List<string>();
        var areaSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var segment = file.Path.Split('/')[0];
            if (areaSeen.Add(segment)) areas.Add(segment);
        }
        foreach (Match match in AreaHint.Matches(text))
            if (areaSeen.Add(match.Groups[1].Value)) areas.Add("area:" + match.Groups[1].Value);
        var truncated = references.ReferencesTruncated || sourceFacts.Files.Length > maxItems || sourceFacts.Symbols.Length > maxItems || areas.Count > maxItems;
        return new(references.CandidateRepository, issue.Title, issue.Body ?? "", files, symbols,
            references.References.Take(maxItems).ToArray(), areas.Take(maxItems).ToArray(),
            new[] { "type", "area", "risk", "complexity", "scope" }, truncated);
    }

    public static TriageFacts Extract(string normalizedIssueText, string repositoryRoot, int maxItems = 50)
    {
        ArgumentNullException.ThrowIfNull(normalizedIssueText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        return ExtractAsync(normalizedIssueText, repositoryRoot, maxItems).GetAwaiter().GetResult();
    }

    public static async Task<TriageFacts> ExtractAsync(string normalizedIssueText, string repositoryRoot, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedIssueText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        string? candidate;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var origin = await Git.TrySingleRemoteUrl(Path.GetFullPath(repositoryRoot), "origin", TimeSpan.FromSeconds(5));
            if (origin is null) candidate = null;
            else
            {
                var target = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(origin);
                candidate = target.Owner + "/" + target.Repository;
            }
        }
        catch { candidate = null; }
        return ExtractReferences(normalizedIssueText, candidate, maxItems);
    }

    static TriageFacts ExtractReferences(string text, string? candidate, int maxItems)
    {
        var found = new List<(int Index, LinkedIssueReference Reference)>();
        foreach (Match match in HttpsReference.Matches(text))
            if (int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                found.Add((match.Index, new(match.Groups[1].Value, match.Groups[2].Value, number, match.Groups[3].Value.Equals("pull", StringComparison.OrdinalIgnoreCase) ? "pull-request" : "issue")));
        foreach (Match match in QualifiedReference.Matches(text))
            if (int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                found.Add((match.Index, new(match.Groups[1].Value, match.Groups[2].Value, number, "issue-or-pr")));
        if (candidate is not null)
        {
            var parts = candidate.Split('/');
            foreach (Match match in LocalReference.Matches(text))
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    found.Add((match.Index, new(parts[0], parts[1], number, "issue-or-pr")));
        }
        var references = new List<LinkedIssueReference>();
        var seen = new HashSet<LinkedIssueReference>();
        foreach (var item in found.OrderBy(item => item.Index)) if (seen.Add(item.Reference)) references.Add(item.Reference);
        return new(candidate, references.Take(maxItems).ToArray(), references.Count > maxItems);
    }
}

/// <summary>Extracts explicit repository file and C# symbol references from normalized issue text.</summary>
public static class IssueReferenceExtractor
{
    static readonly Regex InlineCode = new(@"(?<!`)`([^`\r\n]+)`(?!`)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex MarkdownLink = new(@"\[[^\]\r\n]*\]\(([^\s)]+)(?:\s+[^)]*)?\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Symbol = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\(\))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex LineSuffix = new(@"(?::([1-9][0-9]*)(?:-([1-9][0-9]*))?|#L([1-9][0-9]*)(?:-L([1-9][0-9]*))?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IssueReferenceFacts Extract(GitHubIssue issue, string repositoryRoot, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (maxItems < 1) throw new ArgumentOutOfRangeException(nameof(maxItems));
        var root = Path.GetFullPath(repositoryRoot);
        var text = issue.Title + "\n" + (issue.Body ?? "");
        var spans = InlineCode.Matches(text).Cast<Match>().Select(match => (Match: match, Value: match.Groups[1].Value)).ToArray();
        var files = new List<IssueFileReference>();
        var symbols = new List<IssueSymbolReference>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        void AddFile(string raw)
        {
            if (files.Count >= maxItems || !TryFile(raw, out var path, out var start, out var end) || !seenFiles.Add(path)) return;
            var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
            files.Add(new(path, start, end, File.Exists(fullPath) || Directory.Exists(fullPath)));
        }
        var references = spans.Select(item => (item.Match.Index, item.Value, IsCode: true))
            .Concat(MarkdownLink.Matches(text).Cast<Match>().Select(match => (match.Index, match.Groups[1].Value, IsCode: false)))
            .OrderBy(item => item.Index);
        foreach (var item in references)
        {
            if (TryFile(item.Value, out _, out _, out _) && (!Symbol.IsMatch(item.Value) || HasFileExtension(item.Value))) { AddFile(item.Value); continue; }
            if (item.IsCode && symbols.Count < maxItems && Symbol.IsMatch(item.Value) && seenSymbols.Add(item.Value)) symbols.Add(new(item.Value));
        }
        return new(files.ToArray(), symbols.ToArray());
    }

    static bool HasFileExtension(string value) => Regex.IsMatch(value, @"\.(?:cs|csx|md|json|ya?ml|txt|xml|html|css|js|ts|sh|ps1|sln|csproj|props|targets)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool TryFile(string raw, out string path, out int? start, out int? end)
    {
        path = ""; start = end = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains(' ') || Uri.TryCreate(raw, UriKind.Absolute, out _)) return false;
        var value = raw.Replace('\\', '/');
        var suffix = LineSuffix.Match(value);
        if (suffix.Success)
        {
            var first = suffix.Groups[1].Success ? suffix.Groups[1] : suffix.Groups[3];
            var last = suffix.Groups[2].Success ? suffix.Groups[2] : suffix.Groups[4];
            if (!int.TryParse(first.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var firstLine) || (last.Success && !int.TryParse(last.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
            start = firstLine;
            end = last.Success ? int.Parse(last.Value, CultureInfo.InvariantCulture) : firstLine;
            if (end < start) return false;
            value = value[..suffix.Index];
        }
        if (value.Length == 0 || value.Contains(':') || value.Contains('#') || value.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(value, @"^[A-Za-z]:") || value.Split('/').Any(part => part is "" or "." or "..")) return false;
        if (!value.Contains('/') && !Path.HasExtension(value)) return false;
        path = value;
        return true;
    }
}

public sealed record GitHubIssueLabelWriteResult(string Status, string TargetRepository, int IssueNumber, IReadOnlyList<string> Labels, string? Reason);

/// <summary>Adds selected configured area, risk, and complexity labels to the canonical origin issue without replacing existing labels.</summary>
public sealed class GitHubIssueLabelWriter(IGitHubWriteClient client, GitHubIssueReader issueReader)
{
    public async Task<GitHubIssueLabelWriteResult> AddConfiguredStatusLabelAsync(
        string currentOriginRepository, int issueNumber, string label, AgentTool.GitHubCapabilities capabilities,
        JsonElement labelCatalog, CancellationToken cancellationToken = default)
    {
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        if (currentOriginRepository is null || !Regex.IsMatch(currentOriginRepository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
            return Reject("review", currentOriginRepository, issueNumber, "invalid canonical origin repository");
        if (!capabilities.Capabilities.Any(item => item.Operation == "issues.labels.write" && item.TargetRepository == currentOriginRepository && item.State == "allowed"))
            return Reject("review", currentOriginRepository, issueNumber, "issue label write capability is not allowed");
        if (!labelCatalog.TryGetProperty("labels", out var configured) || configured.ValueKind != JsonValueKind.Array ||
            !configured.EnumerateArray().Any(item => item.GetProperty("name").GetString() == label && item.GetProperty("family").GetString() == "status"))
            return Reject("failure", currentOriginRepository, issueNumber, "status label is not configured");
        var parts = currentOriginRepository.Split('/', 2);
        var current = await issueReader.ReadIssueLabelNamesAsync(parts[0], parts[1], issueNumber, cancellationToken);
        if (current.Contains(label, StringComparer.Ordinal)) return new("already-present", currentOriginRepository, issueNumber, [label], null);
        using var content = new StringContent(JsonSerializer.Serialize(new { labels = new[] { label } }));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{currentOriginRepository}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}/labels"), content, cancellationToken);
        if (!response.IsSuccessStatusCode) return Reject("failure", currentOriginRepository, issueNumber, "GitHub rejected issue label addition");
        var after = await issueReader.ReadIssueLabelNamesAsync(parts[0], parts[1], issueNumber, cancellationToken);
        return after.Contains(label, StringComparer.Ordinal)
            ? new("applied", currentOriginRepository, issueNumber, [label], null)
            : Reject("failure", currentOriginRepository, issueNumber, "issue label postcondition failed");
    }

    public async Task<GitHubIssueLabelWriteResult> RemoveConfiguredReadyLabelAsync(
        string currentOriginRepository, int issueNumber, AgentTool.GitHubCapabilities capabilities,
        JsonElement labelCatalog, CancellationToken cancellationToken = default)
    {
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        if (!IsRepository(currentOriginRepository)) return Reject("review", currentOriginRepository, issueNumber, "invalid canonical origin repository");
        if (!capabilities.Capabilities.Any(item => item.Operation == "issues.labels.write" && item.TargetRepository == currentOriginRepository && item.State == "allowed"))
            return Reject("review", currentOriginRepository, issueNumber, "issue label write capability is not allowed");
        if (!labelCatalog.TryGetProperty("labels", out var configured) || configured.ValueKind != JsonValueKind.Array ||
            !configured.EnumerateArray().Any(item => item.GetProperty("name").GetString() == "READY" && item.GetProperty("family").GetString() == "status"))
            return new("no-op", currentOriginRepository, issueNumber, Array.Empty<string>(), null);
        var parts = currentOriginRepository.Split('/', 2);
        var current = await issueReader.ReadIssueLabelNamesAsync(parts[0], parts[1], issueNumber, cancellationToken);
        if (!current.Contains("READY", StringComparer.Ordinal)) return new("no-op", currentOriginRepository, issueNumber, Array.Empty<string>(), null);
        var endpoint = new Uri($"https://api.github.com/repos/{currentOriginRepository}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}/labels/{Uri.EscapeDataString("READY")}");
        using var response = await client.SendAsync(HttpMethod.Delete, endpoint, cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode) return Reject("failure", currentOriginRepository, issueNumber, "GitHub rejected READY label removal");
        var after = await issueReader.ReadIssueLabelNamesAsync(parts[0], parts[1], issueNumber, cancellationToken);
        return !after.Contains("READY", StringComparer.Ordinal)
            ? new("applied", currentOriginRepository, issueNumber, ["READY"], null)
            : Reject("failure", currentOriginRepository, issueNumber, "READY label removal postcondition failed");
    }

    public async Task<GitHubIssueLabelWriteResult> AddTriageLabelsAsync(
        string currentOriginRepository, string candidateRepository, int issueNumber, TriageDecision decision,
        AgentTool.GitHubCapabilities capabilities, JsonElement labelCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        if (!IsRepository(currentOriginRepository) || !StringComparer.Ordinal.Equals(candidateRepository, currentOriginRepository))
            return Reject("review", candidateRepository, issueNumber, "candidate repository does not match canonical origin");
        var capability = capabilities.Capabilities.SingleOrDefault(item => item.Operation == "issues.labels.write" && item.TargetRepository == currentOriginRepository);
        if (capability?.State != "allowed")
            return Reject("review", currentOriginRepository, issueNumber, "issue label write capability is not allowed");
        var repositoryParts = currentOriginRepository.Split('/', 2);
        var owner = repositoryParts[0];
        var repository = repositoryParts[1];
        var currentLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
        if (currentLabels.Contains("IN_PROGRESS", StringComparer.Ordinal))
            return Reject("review", currentOriginRepository, issueNumber, "issue is already in progress; triage cannot proceed");
        if (!labelCatalog.TryGetProperty("labels", out var configuredLabels) || configuredLabels.ValueKind != JsonValueKind.Array)
            throw new JsonException("Label catalog must contain a labels array.");
        var configured = configuredLabels.EnumerateArray().Select(item =>
            (Name: item.GetProperty("name").GetString(), Family: item.GetProperty("family").GetString())).ToArray();
        var selectedLabels = new List<string>();
        foreach (var family in new[] { "area", "risk", "complexity" })
        {
            var familySelections = decision.Selected.Where(item => item.Family == family).ToArray();
            if (decision.UnresolvedFamilies.Contains(family, StringComparer.Ordinal)) continue;
            if (familySelections.Length > 1 || familySelections.Any(item => item.Labels.Count > (family == "area" ? 20 : 1)))
                return Reject("failure", currentOriginRepository, issueNumber, $"selected {family} labels exceed family cardinality");
            var familyLabels = familySelections.SelectMany(item => item.Labels).Select(item => item.Label).Distinct(StringComparer.Ordinal).ToArray();
            if (familyLabels.Any(label => !configured.Any(item => item.Name == label && item.Family == family)))
                return Reject("failure", currentOriginRepository, issueNumber, $"selected {family} label is not configured");
            selectedLabels.AddRange(familyLabels);
        }
        if (selectedLabels.Count == 0)
        {
            var verifiedLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
            return verifiedLabels.Contains("IN_PROGRESS", StringComparer.Ordinal)
                ? Reject("review", currentOriginRepository, issueNumber, "issue became in progress during triage")
                : Reject("review", currentOriginRepository, issueNumber, "no applicable label was selected; human handling is required");
        }
        var labels = selectedLabels.Distinct(StringComparer.Ordinal).ToArray();
        using var content = new StringContent(JsonSerializer.Serialize(new { labels }));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var response = await client.SendAsync(HttpMethod.Post,
            new Uri($"https://api.github.com/repos/{currentOriginRepository}/issues/{issueNumber.ToString(CultureInfo.InvariantCulture)}/labels"), content, cancellationToken);
        using (response)
        {
            if (!response.IsSuccessStatusCode) return Reject("failure", currentOriginRepository, issueNumber, "GitHub rejected issue label addition");
        }
        var postconditionLabels = await issueReader.ReadIssueLabelNamesAsync(owner, repository, issueNumber, cancellationToken);
        if (postconditionLabels.Contains("IN_PROGRESS", StringComparer.Ordinal))
            return Reject("review", currentOriginRepository, issueNumber, "issue became in progress during triage");
        return new("applied", currentOriginRepository, issueNumber, labels, null);
    }

    static bool IsRepository(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    static GitHubIssueLabelWriteResult Reject(string status, string? repository, int issue, string reason) =>
        new(status, repository ?? string.Empty, issue, Array.Empty<string>(), reason);
}

public sealed record CiRerunResult(string Status, string? Repository, string? ProviderRunId, string? ProviderJobId, string? Reason);

/// <summary>Coordinates only explicit, evidence-backed single-job reruns.</summary>
public interface IGitHubAuthorizationProbe
{
    Task<AgentTool.GitHubCapabilities> ProbeAsync(string root, CancellationToken cancellationToken = default);
}

public sealed class CiRerunCoordinator(IGitHubAuthorizationProbe authorizationProbe, IGitHubWriteClient writeClient)
{
    public async Task<CiRerunResult> RerunAsync(string runDirectory, Guid runId, string currentCommitSha, string root, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Ineligible("rerun reason is required");
        reason = Secrets.Redact(reason.Trim());
        if (reason.Length > 256) reason = reason[..256];
        var events = LocalRunEventStore.Read(runDirectory, runId);
        var evidence = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "ci-failure-evidence");
        if (evidence.ValueKind == JsonValueKind.Undefined) return Ineligible("persisted failure evidence is missing");
        var repositories = events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
            item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() == "repository")
            .Select(item => item.GetProperty("identifier").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        if (repositories.Length != 1 || !Regex.IsMatch(repositories[0], @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
            return Ineligible("persisted canonical repository identity is missing or ambiguous");
        var repository = repositories[0];
        var commit = evidence.GetProperty("commitSha").GetString()!;
        var run = evidence.GetProperty("providerRunId").GetString()!;
        var job = evidence.GetProperty("providerJobId").GetString()!;
        var failureClass = evidence.GetProperty("failureClass").GetString()!;
        if (!Regex.IsMatch(currentCommitSha ?? "", @"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant) || !string.Equals(commit, currentCommitSha, StringComparison.OrdinalIgnoreCase))
            return Ineligible("failure evidence commit is stale");
        if (failureClass is not ("timeout" or "infrastructure")) return Ineligible("failure class is not retryable by default");
        var signature = LocalRunEventStore.ComputeCiFailureSignature(repository, commit, run, job, failureClass);
        if (events.Any(item => item.GetProperty("eventType").GetString() == "ci-rerun" && item.GetProperty("failureSignature").GetString() == signature))
            return Ineligible("this failure identity already has a rerun event");
        var capability = await authorizationProbe.ProbeAsync(root, cancellationToken);
        if (!capability.Capabilities.Any(item => item.Operation == "workflow-rerun" && item.TargetRepository == repository && item.State == "allowed"))
            return Ineligible("workflow-rerun capability is not allowed");
        using var response = await new GitHubActionsJobRerunWriter(writeClient).RerunAsync(repository, job, cancellationToken);
        var mode = "job";
        if ((int)response.StatusCode is 404 or 410)
        {
            response.Dispose();
            using var fallback = await new GitHubActionsFailedJobsRerunWriter(writeClient).RerunAsync(repository, run, cancellationToken);
            if (!fallback.IsSuccessStatusCode) return Ineligible("GitHub rejected the failed-jobs rerun");
            mode = "failed-jobs";
        }
        else if (!response.IsSuccessStatusCode) return Ineligible("GitHub rejected the job rerun");
        LocalRunEventStore.AppendCiRerunEvent(runDirectory, runId, repository, commit, run, job, mode, reason);
        return new("rerun-requested", repository, run, job, null);

        CiRerunResult Ineligible(string reason) => new("not-eligible", null, null, null, reason);
    }
}

public sealed record StartWorkRequest(Guid ProductRunId, string RepositoryRoot, int SourceIssueNumber,
    string BaseRef, string ExpectedBaseSha, string BranchName);

public sealed record StartWorkResult(Guid ProductRunId, string Repository, int SourceIssueNumber,
    string BaseSha, string BranchName, string State);

/// <summary>Checks the chosen start-work target before recording its durable STARTING state.</summary>
public sealed class StartWorkCoordinator(GitHubIssueReader issueReader, AgentTool.GitHubAuthorizationProbe authorizationProbe, IStartWorkGitProcess gitProcess, IGitHubWriteClient writeClient, GitHubIssueLabelWriter labelWriter)
{
    public async Task<string> BootstrapAsync(string repositoryRoot, Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID must be a UUID.", nameof(runId));
        var root = Path.GetFullPath(repositoryRoot);
        var state = await Git.State(root);
        if (state.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
        var before = await Git.Head(root);
        var runDirectory = Path.Combine(root, ".sdeveng", "runs");
        var result = await gitProcess.TryEmptyBootstrapCommit(root);
        string mode;
        string resultValue;
        if (result.ExitCode == 0)
        {
            mode = "empty-commit";
            resultValue = await Git.Head(root);
        }
        else
        {
            var after = await Git.Head(root);
            if (after != before) throw new InvalidOperationException("Empty-commit command failed after changing HEAD; refusing marker fallback.");
            var relativeMarker = ".sdeveng/bootstrap/" + runId.ToString("D") + ".json";
            var marker = Path.Combine(root, relativeMarker.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var contents = JsonSerializer.Serialize(new { schemaVersion = 1, runId = runId.ToString("D"), purpose = "draft-pr-bootstrap" }, AgentTool.Json) + "\n";
            if (File.Exists(marker) && File.ReadAllText(marker) != contents)
                throw new InvalidOperationException("Bootstrap marker is not owned by this run.");
            await File.WriteAllTextAsync(marker, contents, new UTF8Encoding(false), cancellationToken);
            var add = await gitProcess.StageBootstrapMarker(root, relativeMarker);
            if (add.ExitCode != 0) throw new IOException("Unable to stage the owned bootstrap marker: " + Secrets.Redact(add.Output.Trim()));
            var commit = await gitProcess.CommitBootstrapMarker(root, relativeMarker);
            if (commit.ExitCode != 0) throw new IOException("Unable to commit the owned bootstrap marker: " + Secrets.Redact(commit.Output.Trim()));
            mode = "marker";
            resultValue = await Git.Head(root);
        }
        LocalRunEventStore.AppendStartWorkProgress(runDirectory, runId, "bootstrap-created", "completed", mode);
        return resultValue;
    }

    private async Task CleanupBootstrapMarkerAsync(string root, string directory, Guid runId, CancellationToken cancellationToken)
    {
        var events = LocalRunEventStore.Read(directory, runId);
        var bootstrap = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "start-work-progress" &&
            item.GetProperty("operation").GetString() == "bootstrap-created" && item.GetProperty("status").GetString() == "completed");
        if (bootstrap.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Bootstrap mode is not persisted.");
        var mode = bootstrap.GetProperty("detail").GetString();
        if (mode == "empty-commit") return;
        if (mode != "marker") throw new InvalidOperationException("Persisted bootstrap mode is invalid.");
        if (events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == "bootstrap-cleanup" && item.GetProperty("status").GetString() == "completed"))
        {
            var markerPath = Path.Combine(root, ".sdeveng", "bootstrap", runId.ToString("D") + ".json");
            if (File.Exists(markerPath)) throw new InvalidOperationException("Completed bootstrap marker cleanup is inconsistent.");
            return;
        }
        var relative = ".sdeveng/bootstrap/" + runId.ToString("D") + ".json";
        var marker = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var expected = JsonSerializer.Serialize(new { schemaVersion = 1, runId = runId.ToString("D"), purpose = "draft-pr-bootstrap" }, AgentTool.Json) + "\n";
        if (!File.Exists(marker) || !string.Equals(await File.ReadAllTextAsync(marker, cancellationToken), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Bootstrap marker path or content is not owned by this run.");
        if ((await Git.ChangedPaths(root, staged: true)).Paths.Length != 0) throw new InvalidOperationException("Unrelated staged changes prevent bootstrap marker cleanup.");
        File.Delete(marker);
        var add = await gitProcess.StageBootstrapRemoval(root, relative);
        if (add.ExitCode != 0) throw new IOException("Unable to stage bootstrap marker removal: " + Secrets.Redact(add.Output.Trim()));
        var commit = await gitProcess.CommitBootstrapRemoval(root, relative);
        if (commit.ExitCode != 0) throw new IOException("Unable to commit bootstrap marker removal: " + Secrets.Redact(commit.Output.Trim()));
        var branch = await Git.CurrentBranch(root);
        var push = await Git.PushOwnedBranch(root, "origin", branch);
        if (push.ExitCode != 0) throw new IOException("Bootstrap cleanup push failed: " + Secrets.Redact(push.Output.Trim()));
        if (File.Exists(marker)) throw new IOException("Bootstrap marker remained after cleanup commit.");
        LocalRunEventStore.AppendStartWorkProgress(directory, runId, "bootstrap-cleanup", "completed", relative);
    }

    public async Task<StartWorkResult> ContinueAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.RepositoryRoot);
        var directory = Path.Combine(root, ".sdeveng", "runs");
        var events = LocalRunEventStore.Read(directory, request.ProductRunId);
        var stateEvent = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        var currentState = stateEvent.ValueKind == JsonValueKind.Undefined ? null : stateEvent.GetProperty("toState").GetString();
        if (currentState == "IN_PROGRESS")
        {
            var repository = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("identifierType").GetString() == "repository").GetProperty("identifier").GetString();
            return new(request.ProductRunId, repository ?? string.Empty, request.SourceIssueNumber, request.ExpectedBaseSha, request.BranchName, "IN_PROGRESS");
        }
        if (currentState is not ("STARTING" or "STARTING_RETRYABLE"))
            throw new InvalidOperationException("Branch push requires a validated STARTING run.");
        var activeOperation = events.Any(item => item.GetProperty("eventType").GetString() is "start-work-progress" or "operation-completed" &&
            item.GetProperty("operation").GetString() == "branch-created" &&
            (item.GetProperty("eventType").GetString() == "operation-completed" || item.GetProperty("status").GetString() == "completed"))
            ? "branch-pushed" : "branch-created";
        try
        {
            await Git.ValidateBranch(root, request.BranchName);
            var baseSha = await Git.ResolveCommit(root, request.BaseRef);
            if (!string.Equals(baseSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected base SHA is stale.");
            var origin = (await Git.RemoteUrls(root, "origin")).Distinct(StringComparer.Ordinal).ToArray();
            if (origin.Length != 1) throw new InvalidOperationException("Canonical origin must have one GitHub URL.");
            var (owner, repository) = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(origin[0]);
            var target = $"{owner}/{repository}";
            string? Persisted(string system, string type) => events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("externalSystem").GetString() == system && item.GetProperty("identifierType").GetString() == type)
                .Select(item => item.GetProperty("identifier").GetString()).LastOrDefault();
            bool HasIdentifier(string system, string type, string value) => events.Any(item =>
                item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("externalSystem").GetString() == system && item.GetProperty("identifierType").GetString() == type && item.GetProperty("identifier").GetString() == value);
            if (!HasIdentifier("git", "repository", target) || !HasIdentifier("github", "issue", request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("STARTING target does not match its recorded identifiers.");
            if (events.Any(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("identifierType").GetString() == "branch" && item.GetProperty("identifier").GetString() != request.BranchName))
                throw new InvalidOperationException("STARTING run already owns a different branch.");
            if (currentState == "STARTING_RETRYABLE")
                LocalRunEventStore.AppendTransition(directory, request.ProductRunId, currentState, "STARTING");
            bool Completed(string operation) => events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == operation && item.GetProperty("status").GetString() == "completed") ||
                events.Any(item => item.GetProperty("eventType").GetString() == "operation-completed" && item.GetProperty("operation").GetString() == operation);
            var gitState = await Git.State(root);
            if (gitState.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
            var branchRef = "refs/heads/" + request.BranchName;
            var branchSha = await Git.BranchSha(root, request.BranchName);
            if (branchSha is not null)
            {
                var bootstrapRecorded = events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" &&
                    item.GetProperty("operation").GetString() == "bootstrap-created" && item.GetProperty("status").GetString() == "completed");
                var cleanupRecorded = events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" &&
                    item.GetProperty("operation").GetString() == "bootstrap-cleanup" && item.GetProperty("status").GetString() == "completed");
                var expectedBootstrapCommits = cleanupRecorded ? 2 : 1;
                var validBootstrapBranch = bootstrapRecorded &&
                    await Git.CommitCount(root, baseSha, branchRef) == expectedBootstrapCommits &&
                    await Git.IsAncestor(root, baseSha, branchRef);
                if ((branchSha != baseSha && !validBootstrapBranch) || !await Git.IsOwnedBranch(root, request.BranchName))
                    throw new InvalidOperationException("Existing branch is not the owned branch at the verified base.");
            }
            else
            {
                if (Completed("branch-created")) throw new InvalidOperationException("Recorded owned branch is missing.");
                if (gitState.Head != baseSha) throw new InvalidOperationException("HEAD must match the verified base.");
                if (await Git.HasUserChangesOutsideRunStore(root)) throw new InvalidOperationException("Worktree has unrelated changes.");
                await Git.CreateOwnedBranch(root, request.BranchName, baseSha);
            }
            await Git.CheckoutOwnedBranch(root, request.BranchName);
            if (await Git.CommitCount(root, baseSha, branchRef) == 0)
            {
                await BootstrapAsync(root, request.ProductRunId, cancellationToken);
                if (await Git.BranchSha(root, request.BranchName) is null) throw new InvalidOperationException("Bootstrap did not create the owned branch commit.");
            }
            if (!HasIdentifier("git", "branch", request.BranchName)) LocalRunEventStore.AppendBranchIdentifier(directory, request.ProductRunId, request.BranchName);
            if (!Completed("branch-created")) LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "branch-created", "completed");
            activeOperation = "branch-pushed";
            if (!Completed("branch-pushed"))
            {
                var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                if (!capabilities.Capabilities.Any(item => item.Operation == "branch-push" && item.TargetRepository == target && item.State == "allowed"))
                    throw new InvalidOperationException("Branch push capability is not allowed.");
                var remoteSha = await Git.RemoteBranchSha(root, "origin", request.BranchName);
                if (remoteSha is not null && remoteSha != baseSha)
                    throw new InvalidOperationException("Remote branch differs from the owned base; preserving it for recovery.");
                var ownedBranchSha = await Git.BranchSha(root, request.BranchName);
                if (!string.Equals(remoteSha, ownedBranchSha, StringComparison.OrdinalIgnoreCase))
                {
                    var push = await Git.PushOwnedBranch(root, "origin", request.BranchName);
                    if (push.ExitCode != 0) throw new IOException("Branch push failed; the owned local branch remains available: " + Secrets.Redact(push.Output.Trim()));
                }
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "branch-pushed", "completed");
            }
            activeOperation = "pr-created";
            var persistedPr = events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" &&
                item.GetProperty("externalSystem").GetString() == "github" && item.GetProperty("identifierType").GetString() is "pull-request-number" or "pull-request")
                .Select(item => item.GetProperty("identifier").GetString()).LastOrDefault();
            if (Completed("pr-created") || persistedPr is not null)
            {
                if (persistedPr is null || !int.TryParse(persistedPr, NumberStyles.None, CultureInfo.InvariantCulture, out var existingNumber))
                    throw new InvalidOperationException("Completed pull request progress has no valid identifier.");
                var existing = await issueReader.ReadPullRequestAsync(owner, repository, existingNumber, cancellationToken);
                if (existing.HeadBranch != request.BranchName || existing.BaseBranch != request.BaseRef || !existing.IsDraft ||
                    existing.Body?.Contains($"<!-- sdeveng-run:{request.ProductRunId:D} -->", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Persisted pull request does not match this run.");
                if (!HasIdentifier("git", "repository", target) || !HasIdentifier("github", "issue", request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)) ||
                    events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "git" && item.GetProperty("identifierType").GetString() == "repository")
                        .Any(item => item.GetProperty("identifier").GetString() != target) ||
                    events.Where(item => item.GetProperty("eventType").GetString() == "external-identifier-recorded" && item.GetProperty("externalSystem").GetString() == "github" && item.GetProperty("identifierType").GetString() == "issue")
                        .Any(item => item.GetProperty("identifier").GetString() != request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)))
                    throw new InvalidOperationException("Persisted pull request source issue or repository does not match this run.");
                if (!Completed("pr-linked"))
                {
                    var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                    if (!capabilities.Capabilities.Any(item => item.Operation == "pr-create" && item.TargetRepository == target && item.State == "allowed"))
                        throw new InvalidOperationException("Pull request edit capability is not allowed.");
                    var marker = $"<!-- sdeveng-source-issue:{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)} -->";
                    var reference = $"Refs #{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)}";
                    var body = existing.Body ?? string.Empty;
                    var updatedBody = body;
                    if (!body.Contains(marker, StringComparison.Ordinal)) updatedBody = updatedBody.TrimEnd() + (updatedBody.Length == 0 ? "" : "\n\n") + marker;
                    if (!Regex.IsMatch(updatedBody, $@"(?m)^\s*Refs #{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)}\s*$", RegexOptions.CultureInvariant))
                        updatedBody = updatedBody.TrimEnd() + (updatedBody.Length == 0 ? "" : "\n\n") + reference;
                    if (!string.Equals(updatedBody, body, StringComparison.Ordinal))
                    {
                        using var content = new StringContent(JsonSerializer.Serialize(new { body = updatedBody }));
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        using var response = await writeClient.SendAsync(HttpMethod.Patch, new Uri($"https://api.github.com/repos/{target}/pulls/{existingNumber.ToString(CultureInfo.InvariantCulture)}"), content, cancellationToken);
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub rejected draft pull request source issue linkage.");
                    }
                    LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "pr-linked", "completed", persistedPr);
                }
            }
            else
            {
                var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                if (!capabilities.Capabilities.Any(item => item.Operation == "pr-create" && item.TargetRepository == target && item.State == "allowed"))
                    throw new InvalidOperationException("Pull request creation capability is not allowed.");
                var issue = await issueReader.ReadIssueAsync(owner, repository, request.SourceIssueNumber, cancellationToken);
                var title = issue.Title.Trim();
                if (title.Length > 256) title = title[..256];
                if (title.Length == 0) throw new InvalidOperationException("Source issue title is empty.");
                var body = $"<!-- sdeveng-run:{request.ProductRunId:D} -->";
                using var content = new StringContent(JsonSerializer.Serialize(new { title, head = request.BranchName, @base = request.BaseRef, draft = true, body }));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await writeClient.SendAsync(HttpMethod.Post, new Uri($"https://api.github.com/repos/{target}/pulls"), content, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub rejected draft pull request creation.");
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var number = document.RootElement.GetProperty("number").GetInt32();
                if (number <= 0) throw new JsonException("GitHub returned an invalid pull request number.");
                persistedPr = number.ToString(CultureInfo.InvariantCulture);
                LocalRunEventStore.AppendConfirmedPullRequestIdentity(directory, request.ProductRunId, target, number);
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "pr-created", "completed", persistedPr);
            }
            await CleanupBootstrapMarkerAsync(root, directory, request.ProductRunId, cancellationToken);
            activeOperation = "metadata-persisted";
            events = LocalRunEventStore.Read(directory, request.ProductRunId);
            var persistedRepository = Persisted("git", "repository");
            var persistedBranch = Persisted("git", "branch");
            var persistedIssue = Persisted("github", "issue");
            var persistedPullRequest = Persisted("github", "pull-request-number") ?? Persisted("github", "pull-request");
            var persistedPullRequestUrl = Persisted("github", "pull-request-url");
            if (persistedRepository != target || persistedBranch != request.BranchName || persistedIssue != request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture) ||
                !int.TryParse(persistedPullRequest, NumberStyles.None, CultureInfo.InvariantCulture, out var verifiedPrNumber) || verifiedPrNumber <= 0 ||
                string.IsNullOrWhiteSpace(persistedPullRequestUrl) ||
                !Completed("branch-pushed") || !events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == "bootstrap-created" && item.GetProperty("status").GetString() == "completed") ||
                events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == "bootstrap-created" && item.GetProperty("detail").GetString() == "marker") &&
                !Completed("bootstrap-cleanup"))
                throw new InvalidOperationException("Persisted startup identities are incomplete or inconsistent.");
            var localBranch = await Git.BranchSha(root, persistedBranch);
            if (localBranch is null || !Regex.IsMatch(localBranch, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
                throw new InvalidOperationException("Owned local branch SHA is unavailable.");
            var verificationRemote = await Git.RemoteBranchSha(root, "origin", persistedBranch);
            if (!string.Equals(verificationRemote, localBranch, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Remote branch is missing or differs from the owned local branch SHA.");
            var pullRequest = await issueReader.ReadPullRequestAsync(owner, repository, verifiedPrNumber, cancellationToken);
            if (pullRequest.Number != verifiedPrNumber || pullRequest.HeadBranch != persistedBranch || pullRequest.BaseBranch != request.BaseRef ||
                !pullRequest.IsDraft || pullRequest.IsMerged || !string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Persisted pull request is not the matching open draft pull request.");
            if (!string.Equals(pullRequest.HtmlUrl.AbsoluteUri, persistedPullRequestUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("Persisted pull request URL does not match the verified pull request.");
            var sourceIssue = await issueReader.ReadIssueAsync(owner, repository, request.SourceIssueNumber, cancellationToken);
            if (sourceIssue.Number != request.SourceIssueNumber ||
                !string.Equals(sourceIssue.HtmlUrl.AbsoluteUri, $"https://github.com/{target}/issues/{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal))
                throw new InvalidOperationException("Source issue does not match the persisted startup target.");
            if (!Completed("pr-linked")) return new(request.ProductRunId, target, request.SourceIssueNumber, baseSha, request.BranchName, "STARTING");
            if (!events.Any(item => item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("operation").GetString() == "metadata-persisted" && item.GetProperty("status").GetString() == "completed"))
            {
                var capabilities = await authorizationProbe.ProbeAsync(root, cancellationToken);
                using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AgentTool.FindToolkit(), "config", "labels.json"), cancellationToken));
                var labelResult = await labelWriter.AddConfiguredStatusLabelAsync(target, request.SourceIssueNumber, "IN_PROGRESS", capabilities, catalog.RootElement, cancellationToken);
                if (labelResult.Status is not ("applied" or "already-present")) throw new HttpRequestException(labelResult.Reason ?? "GitHub rejected the startup label addition.");
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, "metadata-persisted", "completed", "IN_PROGRESS");
            }
            using (var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AgentTool.FindToolkit(), "config", "labels.json"), cancellationToken)))
            {
                var cleanup = await labelWriter.RemoveConfiguredReadyLabelAsync(target, request.SourceIssueNumber, await authorizationProbe.ProbeAsync(root, cancellationToken), catalog.RootElement, cancellationToken);
                if (cleanup.Status is not ("applied" or "no-op")) throw new HttpRequestException(cleanup.Reason ?? "GitHub rejected READY label removal.");
            }
            LocalRunEventStore.AppendTransition(directory, request.ProductRunId, "STARTING", "IN_PROGRESS");
            return new(request.ProductRunId, target, request.SourceIssueNumber, baseSha, request.BranchName, "IN_PROGRESS");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var progress = LocalRunEventStore.Read(directory, request.ProductRunId);
            if (progress.Any(item => item.GetProperty("eventType").GetString() == "operation-completed" ||
                item.GetProperty("eventType").GetString() == "start-work-progress" && item.GetProperty("status").GetString() == "completed"))
            {
                var terminal = error is InvalidOperationException;
                LocalRunEventStore.AppendStartWorkProgress(directory, request.ProductRunId, activeOperation, terminal ? "terminal-failure" : "retryable-failure", error.Message);
                var latest = progress.Last(item => item.GetProperty("eventType").GetString() == "state-transition").GetProperty("toState").GetString();
                LocalRunEventStore.AppendTransition(directory, request.ProductRunId, latest, terminal ? "STARTING_FAILED" : "STARTING_RETRYABLE");
            }
            throw;
        }
    }

    public async Task<StartWorkResult> StartAsync(StartWorkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProductRunId == Guid.Empty) throw new ArgumentException("Product run ID must be a UUID.", nameof(request));
        if (request.SourceIssueNumber <= 0) throw new ArgumentException("Source issue number must be positive.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RepositoryRoot)) throw new ArgumentException("Repository root is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BranchName) || request.BranchName.StartsWith('-')) throw new ArgumentException("Branch name is invalid.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BaseRef) || request.BaseRef.StartsWith('-')) throw new ArgumentException("Base ref is invalid.", nameof(request));
        if (!Regex.IsMatch(request.ExpectedBaseSha ?? "", @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
            throw new ArgumentException("Expected base SHA must be a full commit ID.", nameof(request));

        var root = Path.GetFullPath(request.RepositoryRoot);
        var state = await Git.State(root);
        if (!string.Equals(state.Root, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Repository root must be the Git worktree root.");
        if (state.Operations.Count != 0) throw new InvalidOperationException("Worktree has an unfinished Git operation.");
        // Product run events live in the worktree; exclude only that store from the clean check.
        if (await Git.HasUserChangesOutsideRunStore(root)) throw new InvalidOperationException("Worktree has changes outside the product run store.");
        await Git.ValidateBranch(root, request.BranchName);
        await Git.ValidateBranch(root, request.BaseRef);
        var urls = (await Git.RemoteUrls(root, "origin")).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length != 1) throw new InvalidOperationException("Canonical origin must have one GitHub URL.");
        var (owner, repository) = AgentTool.GitHubAuthorizationProbe.ParseGitHubTarget(urls[0]);
        var actualSha = await Git.ResolveCommit(root, request.BaseRef);
        if (!string.Equals(actualSha, request.ExpectedBaseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Expected base SHA is stale.");
        var issue = await issueReader.ReadIssueAsync(owner, repository, request.SourceIssueNumber, cancellationToken);
        var expectedIssueUrl = $"https://github.com/{owner}/{repository}/issues/{request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture)}";
        if (issue.Number != request.SourceIssueNumber || !string.Equals(issue.HtmlUrl.AbsoluteUri, expectedIssueUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source issue does not belong to canonical origin.");

        var runDirectory = Path.Combine(root, ".sdeveng", "runs");
        var events = LocalRunEventStore.Read(runDirectory, request.ProductRunId);
        var transition = events.LastOrDefault(item => item.GetProperty("eventType").GetString() == "state-transition");
        if (transition.ValueKind == JsonValueKind.Undefined || transition.GetProperty("toState").GetString() != "created")
            throw new InvalidOperationException("Start work requires an existing created run.");
        LocalRunEventStore.AppendRepositoryIdentifier(runDirectory, request.ProductRunId, $"{owner}/{repository}");
        LocalRunEventStore.AppendIssueIdentifier(runDirectory, request.ProductRunId, request.SourceIssueNumber.ToString(CultureInfo.InvariantCulture));
        LocalRunEventStore.AppendTransition(runDirectory, request.ProductRunId, "created", "STARTING");
        return new(request.ProductRunId, $"{owner}/{repository}", request.SourceIssueNumber, actualSha, request.BranchName, "STARTING");
    }
}

public static class AgentTool
{
    public const string Product = "sdeveng";
    public const string CliVersion = "3.0.0";
    public const int ResultSchemaVersion = 1;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public const string Help = """
        sdeveng — SimplexiDev Engineering Toolkit deterministic → JEV → Codex
        sdeveng <command> [options]

        version | --version
        config explain [--role ROLE] [--measurements FILE] [--model-context TOKENS]
        install | update [--home DIR] [--codex-home DIR] [--dry-run] [--bin]
        uninstall [--home DIR] [--codex-home DIR] [--dry-run]
        doctor
        tools list | skills list | skills explain --input <file>
        repo describe | changed-files [--base REF] | summary [--base REF] | locate (--query TEXT | --path PATH | --name TEXT) | health | hygiene
        repo affected-projects [--base REF] | ownership --file PATH
        git state | summary [--base REF] | conflict-forecast --base REF | prepare-commit
        git stage-owned --paths-file FILE | commit-owned --paths-file FILE --message TEXT
        git issue-start --issue NUMBER --branch NAME
        git branch-create --branch NAME | push-owned --remote NAME --branch NAME
        git worktree-create --branch NAME --path DIR | worktree-remove-owned --path DIR
        git stale-base --base REF --expected SHA | abandon-owned --branch NAME --path DIR
        github pr-status | review-comments --pr NUMBER | prepare-pr | labels [--dry-run | --apply]
        github actions [--run-id NUMBER] [--failed-logs]
        dotnet inspect [--project PATH] | build-plan [--base REF] [--project PATH] [--configuration NAME] [--binlog]
        dotnet semantic-model --project SOLUTION
        dotnet relationships --project SOLUTION --path PROJECT
        dotnet test-candidates --project SOLUTION --symbol KEY
        dotnet affected-symbols --project SOLUTION --symbol KEY
        dotnet affected-files --project SOLUTION --path FILE
        dotnet test-plan [--base REF] [--project PATH] [--configuration NAME]
            [--test NAME | --class NAME | --category NAME | --filter EXPR]
        dotnet diagnostics-plan [--process-id NUMBER] [--signal counters|cpu|contention|allocations|managed-memory|crash|hang]
            [--duration-seconds NUMBER]
        dotnet verify [--base REF] [--project PATH] | format --project PATH [--apply]
        dotnet dependencies --project PATH | package-audit --project PATH | api-check --project PATH | release-verify --project PATH
        release evidence --profile NAME --output REPOSITORY_RELATIVE_PATH [--evidence-file PATH --tag TAG]
        verification decide --hosted-file PATH --commit SHA [--local-file PATH]
        logs summarize --file PATH | sarif summarize --file PATH [--baseline PATH]
        artifact inspect --file PATH | verify --file PATH --sha256 HEX
        test-results summarize --file PATH | coverage summarize --file PATH
        jev noul|choice|score --input PATH [--dry-run] [--safe-input]
        jev screen --input PATH [--dry-run] [--safe-input] | cache-clear
        upstream status | update [--dry-run] | dotnet-skills <status|diff|check> [--dry-run]
        validate | eval [--skill NAME] [--results PATH] | release --output ZIP
        results init | new <audit|handoff|review|report> <name>
        results list [audit|handoff|review|report] [--json] | latest <type> [--json]
        results context <type> [--json] | clean [--dry-run]
        run status <UUID> | explain <UUID> | resume <UUID> | cancel <UUID> | list | abandon <UUID>

        Common: --root DIR (target repository), --toolkit DIR, --set NAME=VALUE (configuration override; repeatable), --json (schema-versioned output), --help
        JEV input: {"capability":"configured-id","purpose":"allowed-purpose","deterministicNarrowed":true,"state":"sanitized excerpt","instructions":"bounded question","criteria":...}
        Screen input: same routing metadata plus {"query":"question","candidates":[{"id":"path","text":"safe excerpt"}]}
        JEV defaults to auto; missing/invalid/uncertain answers return REVIEW for Codex.
        No command merges PRs, force-pushes, rewrites history, cleans unrelated paths, or installs external tools. git push-owned normally pushes one named local branch; git commit-owned creates a local commit from an exact validated staged path set.
        """;

    public static async Task<int> Main(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        var command = "unknown";
        try
        {
            var parsed = Cli.Parse(args);
            var toolkit = FindToolkit(parsed.Get("toolkit"));
            var builder = Host.CreateApplicationBuilder(args);
            var root = Path.GetFullPath(parsed.Get("root") ?? Environment.CurrentDirectory);
            Settings.AddConfigurationSources(builder.Configuration, toolkit);
            foreach (var (name, value) in parsed.ConfigurationOverrides()) builder.Configuration[name] = value;
            Settings.RegisterOptions(builder.Services, builder.Configuration, toolkit, root);
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            AgentToolModule.Register(builder.Services);
            using var host = builder.Build();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            await host.StartAsync(cancellation.Token);
            try
            {
                var c = Cli.Parse(args);
                command = c.Command;
                if (c.Flag("version") || command == "version")
                {
                    var version = Result.Ok(new { kind = "cli-version", schemaVersion = 1, product = Product, version = CliVersion, resultSchemaVersion = ResultSchemaVersion });
                    Console.WriteLine(json ? RenderJson(version, "version", Environment.CurrentDirectory, new()) : $"{Product} {CliVersion}");
                    return 0;
                }
                if (c.Flag("help") || c.Words.Count == 0 || command == "help")
                {
                    Console.WriteLine(json ? RenderJson(Result.Ok(new { help = Help }), "help", Environment.CurrentDirectory, new()) : Help);
                    return 0;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var settings = Settings.LoadFor(toolkit, command, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentTool.RuntimeSettingsOptions>>().Value.Settings);
                var result = await host.Services.GetRequiredService<AgentToolRuntime>().Execute(c, toolkit, root, settings, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Console.WriteLine(json ? RenderJson(result, command, root, settings.Output) : Render(result, root, settings.Output));
                return result.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                await host.StopAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or JsonException or FormatException or System.Xml.XmlException or System.ComponentModel.Win32Exception)
        {
            var result = new Result("error", null, 2);
            Console.Error.WriteLine(json
                ? SerializeEnvelope(result, command, new { code = "invalid-invocation", message = Secrets.Redact(e.Message) })
                : $"sdeveng: {Secrets.Redact(e.Message)}");
            return 2;
        }
        catch (Exception e)
        {
            var result = new Result("error", null, 70);
            Console.Error.WriteLine(json
                ? SerializeEnvelope(result, command, new { code = "internal-error", message = Secrets.Redact(e.Message) })
                : $"sdeveng: internal error: {Secrets.Redact(e.Message)}");
            return 70;
        }
    }

    public sealed class AgentToolRuntime
    {
        private readonly ILogger<AgentToolRuntime> _logger;
        private readonly IEnumerable<ICommandModule> _commandModules;
        public AgentToolRuntime(ILogger<AgentToolRuntime> logger, IEnumerable<ICommandModule> commandModules)
        {
            _logger = logger;
            _commandModules = commandModules;
        }
        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Executing {Command} for {Root}", command.Command, root);
            var module = _commandModules.FirstOrDefault(candidate => candidate.CanHandle(command))
                ?? throw new ArgumentException("Unknown command. Use --help.");
            return module.Execute(command, toolkit, root, settings, cancellationToken);
        }
    }

    public interface ICommandModule
    {
        bool CanHandle(Cli command);
        Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken);
    }

    public static class AgentToolModule
    {
        public static IServiceCollection Register(IServiceCollection services)
        {
            services.AddSingleton<AgentToolRuntime>();
            services.AddSingleton(_ =>
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                client.DefaultRequestHeaders.UserAgent.ParseAdd("sdeveng");
                return client;
            });
            services.AddTransient<GitHubReadClient>();
            services.AddTransient<IGitHubReadClient>(provider => provider.GetRequiredService<GitHubReadClient>());
            services.AddSingleton<IGitHubCredentialProvider, GitHubCredentialProvider>();
            services.AddTransient<GitHubWriteClient>();
            services.AddTransient<IGitHubWriteClient>(provider => provider.GetRequiredService<GitHubWriteClient>());
            services.AddTransient<GitHubIssueLabelWriter>();
            services.AddTransient<IGitHubLabelCatalog, GitHubLabelCatalog>();
            services.AddTransient<GitHubActionsJobRerunWriter>();
            services.AddTransient<GitHubActionsFailedJobsRerunWriter>();
            services.AddTransient<CiRerunCoordinator>();
            services.AddTransient<GitHubCommitReader>();
            services.AddTransient<GitHubRepositoryMetadataReader>();
            services.AddTransient<GitHubIssueReader>();
            services.AddTransient<StartWorkCoordinator>();
            services.AddTransient<GitHubChecksWorkflowReader>();
            services.AddTransient<CiObservationService>();
            services.AddTransient<GitHubActionsReader>();
            services.AddTransient<GitHubPrStatusReader>();
            services.AddTransient<GitHubReviewCommentReader>();
            services.AddSingleton<ITriageSemanticClassifier, AbstainingTriageSemanticClassifier>();
            services.AddSingleton<IGitHubAuthorizationProcess, GitHubAuthorizationProcess>();
            services.AddTransient<GitHubAuthorizationProbe>();
            services.AddTransient<IGitHubAuthorizationProbe>(provider => provider.GetRequiredService<GitHubAuthorizationProbe>());
            services.AddSingleton<ICommandModule, InstallerCommandModule>();
            services.AddSingleton<ICommandModule, ConfigurationCommandModule>();
            services.AddSingleton<ICommandModule, ToolDiscoveryCommandModule>();
            services.AddSingleton<ICommandModule, SkillDiscoveryCommandModule>();
            services.AddSingleton<ICommandModule, DoctorCommandModule>();
            services.AddSingleton<ICommandModule, GitCommandModule>();
            services.AddSingleton<ICommandModule, RepoCommandModule>();
            services.AddSingleton<ICommandModule, GitHubCommandModule>();
            services.AddSingleton<ICommandModule, DotnetCommandModule>();
            services.AddSingleton<ICommandModule, ReportCommandModule>();
            services.AddSingleton<ICommandModule, VerificationCommandModule>();
            services.AddSingleton<ICommandModule, JevCommandModule>();
            services.AddSingleton<ICommandModule, UpstreamCommandModule>();
            services.AddSingleton<ICommandModule, ValidateCommandModule>();
            services.AddSingleton<ICommandModule, EvalCommandModule>();
            services.AddSingleton<ICommandModule, ReleaseCommandModule>();
            services.AddSingleton<ICommandModule, ReleaseValidationCommandModule>();
            services.AddSingleton<ICommandModule, RunCommandModule>();
            services.AddSingleton<ICommandModule, ResultsCommandModule>();
            return services;
        }
    }

    public sealed class ConfigurationCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "config explain";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("config explain");
            if (command.Get("role") is { } role)
            {
                var context = command.Get("model-context") is not null ? command.PositiveInt("model-context") : (int?)null;
                var service = new SdevEng.Infrastructure.ContextBudgetService(Path.Combine(toolkit, "config", "context-budget-policy.json"));
                var explanation = command.Get("measurements") is { } path
                    ? service.ExplainFile(role, Path.GetFullPath(path, root), context)
                    : service.Explain(role, [], context);
                return Task.FromResult(new Result(explanation.Status, explanation, explanation.MandatoryOverflow ? 1 : 0));
            }
            if (command.Get("measurements") is not null || command.Get("model-context") is not null)
                throw new ArgumentException("Budget explain requires --role.");
            return Task.FromResult(Result.Ok(new
            {
                kind = "effective-config",
                schemaVersion = 1,
                settings,
                precedence = new[] { "built-in toolkit JSON", "machine", "user", "repository", "environment", "invocation --set" },
                sources = new
                {
                    files = Settings.SourceFiles(toolkit, root),
                    environmentVariables = new[] { "JEV_MODE", "TYPESAFE_API_URL", "JEV_MODEL", "JEV_TIMEOUT_SECONDS" },
                    invocationOverrides = command.ConfigurationOverrides().Select(pair => pair.Key).ToArray()
                }
            }));
        }
    }

    /// <summary>Lists typed, read-only worker tools backed by the canonical CLI contract manifest.</summary>
    public sealed class ToolDiscoveryCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "tools list";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            command.ValidateCommand("tools list");
            var path = Path.Combine(toolkit, "config", "agent-tool-contracts.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var tools = manifest.RootElement.GetProperty("toolDescriptors").Deserialize<ToolDescriptor[]>(Json)
                ?? throw new JsonException("Tool descriptor catalog is empty.");
            return Result.Ok(new { kind = "tool-descriptors", schemaVersion = 1, tools });
        }
    }

    public sealed class SkillDiscoveryCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "skills list" or "skills explain";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Command == "skills explain")
            {
                command.ValidateCommand("skills explain");
                var path = Path.GetFullPath(command.Get("input") ?? throw new ArgumentException("skills explain requires --input."), root);
                var request = JsonSerializer.Deserialize<SkillCostExplainRequest>(File.ReadAllText(path), Json)
                    ?? throw new ArgumentException("Empty skill-cost explain request.");
                var tokenizers = new SdevEng.Infrastructure.TokenizerRegistry(Path.GetDirectoryName(path)!);
                tokenizers.Register(request.Tokenizer);
                var templates = new SdevEng.Infrastructure.ChatTemplateRegistry(tokenizers);
                templates.Register(request.Template);
                var result = new SdevEng.Infrastructure.SkillCostMeasurementService(new SdevEng.Infrastructure.RenderedInputTokenCounter(templates)).Explain(toolkit, request);
                return Task.FromResult(Result.Ok(result));
            }
            command.ValidateCommand("skills list");
            SkillMetadata[] skills = [];
            var diagnostics = new List<object>();
            try
            {
                skills = SkillCompatibilityMapReader.ReadMetadataIndex(toolkit);
                var errors = new List<string>();
                SkillCompatibilityMapReader.ValidateRequiredTools(toolkit, skills, errors);
                diagnostics.AddRange(errors.Select(message => (object)new { code = "unknown-tool", message }));
                SkillCompatibilityMapReader.ValidateResources(toolkit, skills, verifyHashes: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                diagnostics.Add(new { code = "invalid-skill-catalog", message = e.Message });
            }
            var data = new
            {
                kind = "skill-catalog",
                schemaVersion = 1,
                skills = JsonSerializer.SerializeToNode(skills, new JsonSerializerOptions(Json)
                { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                diagnostics
            };
            return Task.FromResult(new Result(diagnostics.Count == 0 ? "ok" : "error", data, diagnostics.Count == 0 ? 0 : 1));
        }
    }

    public sealed class ValidateCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "validate";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("validate");
            return Task.FromResult(Validation.Run(toolkit));
        }
    }

    public sealed class EvalCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "eval";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("eval");
            return Task.FromResult(Evaluation.Run(toolkit, command.Get("skill"), command.Get("results")));
        }
    }

    public sealed class ReleaseCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "release";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("release");
            return Task.FromResult(Release(toolkit, command.Require("output")));
        }
    }

    public sealed class ResultsCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        ["results init", "results new", "results list", "results latest", "results context", "results clean"];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var words = command.Words.Skip(2).ToArray();
            return name switch
            {
                "results init" => Init(words, root),
                "results new" => await Results.New(root, words),
                "results list" => Results.List(root, words),
                "results latest" => Results.Latest(root, words),
                "results context" => Results.Context(root, words),
                "results clean" => Clean(words, root, command.Flag("dry-run")),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }

        private static Result Init(string[] words, string root)
        {
            Results.RequireWords(words, 0, "Usage: results init.");
            return Results.Init(root);
        }

        private static Result Clean(string[] words, string root, bool dryRun)
        {
            Results.RequireWords(words, 0, "Usage: results clean [--dry-run].");
            return Results.Clean(root, dryRun);
        }
    }

    public sealed class RunCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "run status" or "run explain" or "run resume" or "run cancel" or "run list" or "run abandon";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var words = command.Words.Skip(2).ToArray();
            var directory = Path.Combine(root, ".sdeveng", "runs");
            if (command.Command == "run list")
            {
                if (words.Length != 0) throw new ArgumentException("Usage: run list.");
                return Task.FromResult(Result.Ok(LocalRunEventStore.List(directory)));
            }
            if (words.Length != 1 || !Guid.TryParseExact(words[0], "D", out var runId) || runId == Guid.Empty)
                throw new ArgumentException($"Usage: {command.Command} <UUID>.");
            var result = command.Command switch
            {
                "run status" => LocalRunEventStore.Status(directory, runId),
                "run explain" => LocalRunEventStore.Explain(directory, runId),
                "run resume" => LocalRunEventStore.Resume(directory, runId),
                "run abandon" => LocalRunEventStore.Abandon(directory, runId),
                _ => LocalRunEventStore.Cancel(directory, runId)
            };
            return Task.FromResult(Result.Ok(result));
        }
    }

    public sealed class InstallerCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "install" or "update" or "uninstall";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var home = command.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codexHome = command.Get("codex-home") ?? (command.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null);
            return Task.FromResult(Installer.Run(toolkit, home, codexHome, name, command.Flag("dry-run"), command.Flag("bin")));
        }
    }

    public sealed class DoctorCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "doctor";

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand("doctor");
            return Doctor(toolkit, settings, command);
        }

        private static async Task<Result> Doctor(string toolkit, Settings settings, Cli c)
        {
            var checks = new List<object>(); bool requiredOk = true;
            foreach (var (tool, args, required) in new[] { ("dotnet", new[] { "--version" }, true), ("git", new[] { "--version" }, true), ("gh", new[] { "--version" }, false), ("codex", new[] { "--version" }, false) })
            {
                try
                {
                    var r = await Processes.Run(tool, args, toolkit);
                    var ok = r.ExitCode == 0 && (tool != "dotnet" || Version.TryParse(r.Output.Trim().Split('-')[0], out var v) && v.Major >= 10);
                    if (tool == "dotnet")
                    {
                        ProcessResult runtimeResult;
                        try { runtimeResult = await Processes.Run("dotnet", ["--list-runtimes"], toolkit); }
                        catch (System.ComponentModel.Win32Exception) { runtimeResult = new(-1, ""); }
                        var diagnostics = DotnetDoctorDiagnostics.Evaluate(r.ExitCode, r.Output, runtimeResult.ExitCode, runtimeResult.Output);
                        checks.Add(new { tool, required, available = diagnostics.SdkAvailable, summary = Output.Compact(r.Output, settings.Output), sdkAvailable = diagnostics.SdkAvailable, sdkVersion = diagnostics.SdkVersion, runtimeAvailable = diagnostics.RuntimeAvailable, runtimes = diagnostics.Runtimes });
                        if (!diagnostics.SdkAvailable || !diagnostics.RuntimeAvailable) requiredOk = false;
                        continue;
                    }
                    if (tool == "gh")
                    {
                        ProcessResult authResult;
                        try { authResult = await GitHubAuthorizationTransport.AuthStatus(toolkit); }
                        catch (System.ComponentModel.Win32Exception) { authResult = new(-1, ""); }
                        var diagnostics = GitHubDoctorDiagnostics.Evaluate(ok, authResult.ExitCode);
                        checks.Add(new { tool, required, available = diagnostics.Available, authenticated = diagnostics.Authenticated, summary = Output.Compact(r.Output, settings.Output) });
                        continue;
                    }
                    checks.Add(new { tool, required, available = ok, summary = Output.Compact(r.Output, settings.Output) });
                    if (required && !ok) requiredOk = false;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    checks.Add(tool == "dotnet"
                        ? new { tool, required, available = false, sdkAvailable = false, sdkVersion = (string?)null, runtimeAvailable = false, runtimes = Array.Empty<string>() }
                        : new { tool, required, available = false });
                    if (required) requiredOk = false;
                }
            }
            var home = c.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var codex = c.Get("codex-home") ?? (c.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null) ?? Path.Combine(home, ".codex");
            var classification = DoctorPrerequisiteDiagnostics.Classify(checks, requiredOk);
            var optionalTools = settings.Toolkit.OptionalTools.Select(t => new { name = t, available = Processes.OnPath(t) }).ToArray();
            return new(requiredOk ? "ok" : "failed", new { checks, prerequisites = new { required = new { status = classification.RequiredStatus, checks = classification.Required }, optional = new { status = classification.OptionalStatus, checks = classification.Optional, tools = optionalTools } }, toolkit, codex, skills = Path.Combine(home, ".agents/skills"), installation = Installer.Inspect(codex), jev = new { settings.Jev.Mode, credentials = JevCredentials.Status(), settings.Jev.Model }, upstream = "Run upstream status for integration policy; listed integrations are not automatically installed.", optionalTools }, requiredOk ? 0 : 1);
        }
    }

    internal static class DoctorPrerequisiteDiagnostics
    {
        public sealed record Classification(string RequiredStatus, object[] Required, string OptionalStatus, object[] Optional);

        public static Classification Classify(IEnumerable<object> checks, bool requiredOk)
        {
            var required = new List<object>();
            var optional = new List<object>();
            foreach (var check in checks)
            {
                var isRequired = (bool)check.GetType().GetProperty("required")!.GetValue(check)!;
                (isRequired ? required : optional).Add(check);
            }
            return new(requiredOk ? "ok" : "failed", required.ToArray(), "informational", optional.ToArray());
        }
    }

    internal static class GitHubDoctorDiagnostics
    {
        public sealed record State(bool Available, bool? Authenticated);

        public static State Evaluate(bool available, int authExitCode) =>
            new(available, available ? authExitCode == 0 : null);
    }

    internal static class DotnetDoctorDiagnostics
    {
        public sealed record State(bool SdkAvailable, string? SdkVersion, bool RuntimeAvailable, string[] Runtimes);

        public static State Evaluate(int sdkExitCode, string sdkOutput, int runtimeExitCode, string runtimeOutput)
        {
            var sdkVersion = sdkOutput.Trim();
            var sdkAvailable = sdkExitCode == 0 && Version.TryParse(sdkVersion.Split('-')[0], out var sdk) && sdk.Major >= 10;
            var runtimes = runtimeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal)
                    && Version.TryParse(line["Microsoft.NETCore.App ".Length..].Split(' ')[0], out var version) && version.Major == 10)
                .ToArray();
            return new(sdkAvailable, sdkAvailable ? sdkVersion : null, runtimeExitCode == 0 && runtimes.Length > 0, runtimes);
        }
    }

    public sealed class GitCommandModule : ICommandModule
    {
        private readonly GitHubIssueReader? _issueReader;

        public GitCommandModule(GitHubIssueReader? issueReader = null) => _issueReader = issueReader;

        public bool CanHandle(Cli command) => command.Command is "git state" or "git summary" or "git conflict-forecast" or "git prepare-commit" or "git stage-owned" or "git commit-owned" or "git issue-start" or "git branch-create" or "git worktree-create" or "git push-owned" or "git worktree-remove-owned" or "git stale-base" or "git abandon-owned";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            switch (command.Command)
            {
                case "git state": return Result.Ok(await Git.State(root));
                case "git summary": return Result.Ok(await Repository.Summary(root, command.Get("base"), settings.Output));
                case "git stale-base":
                    var expected = command.Require("expected");
                    if (!Regex.IsMatch(expected, "\\A[0-9a-fA-F]{40,64}\\z")) throw new ArgumentException("Expected base must be a full commit ID.");
                    var baseRef = command.Require("base");
                    var actual = await Git.ResolveCommit(root, baseRef);
                    return Result.Ok(new { baseRef, expected, actual, stale = !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) });
                case "git conflict-forecast": return Result.Ok(await Git.ConflictForecast(root, command.Require("base"), settings.Output));
                case "git prepare-commit":
                    await Git.EnsureSafe(root, false);
                    var whitespace = await Git.WhitespaceCheck(root);
                    return new(whitespace.ExitCode != 0 ? "failed" : "ok", new { state = await Git.State(root), whitespace = Output.Compact(whitespace.Output, settings.Output), next = "Review explicit file scope before staging. Commit/push/PR creation remains caller-controlled; never merge without approval." }, whitespace.ExitCode);
                case "git stage-owned": return Result.Ok(await Git.StageOwned(root, command.Require("paths-file")));
                case "git commit-owned": return Result.Ok(await Git.CommitOwned(root, command.Require("paths-file"), command.Require("message")));
                case "git issue-start":
                    await Git.EnsureSafe(root, true);
                    var issue = command.PositiveInt("issue"); var branch = command.Require("branch");
                    await Git.ValidateBranch(root, branch);
                    var reader = _issueReader ?? throw new InvalidOperationException("GitHub issue reader is unavailable; no branch created.");
                    var originUrl = (await Git.RemoteUrls(root, "origin")).Single();
                    var (owner, repository) = GitHubRepositoryTarget(originUrl);
                    GitHubIssue issueInfo;
                    try { issueInfo = await reader.ReadIssueAsync(owner, repository, issue, cancellationToken); }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
                    { throw new InvalidOperationException("Issue is unavailable or not open; no branch created."); }
                    if (!string.Equals(issueInfo.State, "open", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Issue is unavailable or not open; no branch created.");
                    await AgentTool.Execute(Cli.Parse(["git", "branch-create", "--branch", branch]), toolkit, root, settings);
                    return Result.Ok(new { branch, issue });
                case "git branch-create":
                    var newBranch = command.Require("branch");
                    await Git.SwitchCreateBranch(root, newBranch);
                    return Result.Ok(new { branch = newBranch });
                case "git worktree-create":
                    var worktreeBranch = command.Require("branch");
                    var worktreePath = Path.GetFullPath(command.Require("path"));
                    await Git.CreateWorktree(root, worktreeBranch, worktreePath);
                    return Result.Ok(new { branch = worktreeBranch, path = worktreePath });
                case "git push-owned":
                    var remote = command.Require("remote");
                    var pushBranch = command.Require("branch");
                    var pushState = await Git.State(root);
                    if (pushState.Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no push performed.");
                    var attemptedHead = pushState.Head;
                    var push = await Git.PushNamedBranch(root, remote, pushBranch);
                    if (push.ExitCode != 0) return new("failed", new { remote, branch = pushBranch, head = attemptedHead, recovery = "Push failed; the local branch remains available. Inspect the remote and retry the named branch after resolving the failure.", evidence = Output.Compact(Secrets.Redact(push.Output), settings.Output) }, 1);
                    return Result.Ok(new { remote, branch = pushBranch, head = attemptedHead });
                case "git worktree-remove-owned":
                    var removePath = Path.GetFullPath(command.Require("path"));
                    await Git.RemoveOwnedWorktree(root, removePath);
                    return Result.Ok(new { path = removePath });
                case "git abandon-owned":
                    var abandonBranch = command.Require("branch");
                    var abandonPath = Path.GetFullPath(command.Require("path"));
                    await Git.AbandonOwnedWorktree(root, abandonBranch, abandonPath);
                    return Result.Ok(new { branch = abandonBranch, path = abandonPath, removed = true });
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }

        private static (string Owner, string Repository) GitHubRepositoryTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
                path = uri.AbsolutePath;
            }
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
            var parts = path.Trim('/').TrimEnd('/').Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) throw new InvalidOperationException("Origin is not a GitHub repository; no branch created.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
    }

    public sealed class RepoCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command is "repo describe" or "repo changed-files" or "repo summary" or "repo locate" or "repo affected-projects" or "repo ownership" or "repo health" or "repo hygiene";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            switch (command.Command)
            {
                case "repo describe": return Result.Ok(await Repository.Describe(root, settings.Output));
                case "repo changed-files": return Result.Ok(await Git.Changed(root, command.Get("base")));
                case "repo summary": return Result.Ok(await Repository.Summary(root, command.Get("base"), settings.Output));
                case "repo locate":
                    var catalog = await Repository.FileCatalog(root);
                    if (!catalog.HasValidIntegrity()) throw new InvalidDataException("Repository file catalog failed its integrity check.");
                    var searchModes = new[] { "query", "path", "name", "exact-text", "fuzzy" }.Where(option => command.Get(option) is not null).ToArray();
                    if (searchModes.Length != 1) throw new ArgumentException("repo locate requires exactly one of --query, --path, --name, --exact-text, or --fuzzy.");
                    var mode = searchModes[0];
                    var query = command.Require(mode);
                    if (mode is "exact-text" or "fuzzy")
                    {
                        var ranked = mode == "exact-text" ? catalog.SearchExactText(query, Math.Min(settings.Output.MaxItems, 50)) : catalog.SearchFuzzyTerms(query, Math.Min(settings.Output.MaxItems, 50));
                        return Result.Ok(new { kind = "repository-file-search", schemaVersion = 1, indexVersion = catalog.IndexVersion, indexIntegrity = "valid", candidates = ranked, total = ranked.Length, limit = Math.Min(settings.Output.MaxItems, 50), scope = mode == "exact-text" ? "Exact text in repository paths, names, and bounded indexed text terms." : "Bounded one-edit fuzzy matching over indexed text terms; at most eight query terms and 50 candidates." });
                    }
                    var matches = mode switch { "path" => catalog.FindExactPath(query), "name" => catalog.FindName(query), _ => catalog.Find(query) };
                    var boundedMatches = matches.Take(settings.Output.MaxItems).ToArray();
                    return Result.Ok(new { kind = "repository-file-search", schemaVersion = 1, indexVersion = catalog.IndexVersion, indexIntegrity = "valid", matches = boundedMatches.Select(file => new { path = file.Path, evidenceKey = RepositoryLocationKey.Create(file.Path) }), total = matches.Length, limit = settings.Output.MaxItems, truncated = matches.Length > boundedMatches.Length, scope = "Git tracked + untracked, nonignored paths, names, and bounded text terms excluding the managed result store; use rg for symbols." });
                case "repo affected-projects":
                    var affected = await Projects.Affected(root, await Git.Changed(root, command.Get("base")));
                    var affectedProjects = affected.Projects.Take(settings.Output.MaxItems).ToArray();
                    var candidateTests = new List<string>();
                    foreach (var path in affected.Projects) if (await Projects.IsTest(root, path)) candidateTests.Add(path);
                    object Explain(string path) => new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), evidenceKey = RepositoryLocationKey.Create(Path.GetRelativePath(root, path)), explanation = affected.ExplanationPaths?.TryGetValue(path, out var chain) == true ? chain.Select(item => Path.GetRelativePath(root, item).Replace('\\', '/')).ToArray() : [affected.Reason] };
                    return Result.Ok(new { kind = "affected-projects", schemaVersion = 1, projects = affectedProjects.Select(Explain), total = affected.Projects.Length, candidateTests = candidateTests.Take(settings.Output.MaxItems).Select(Explain), candidateTestCount = candidateTests.Count, limit = settings.Output.MaxItems, truncated = affected.Projects.Length > affectedProjects.Length, candidateTestsTruncated = candidateTests.Count > settings.Output.MaxItems, reason = affected.Reason });
                case "repo ownership": return Result.Ok(await Projects.Ownership(root, command.Require("file")));
                case "repo health":
                    var health = await Projects.Health(root, settings.Health);
                    return new(health.Count == 0 ? "ok" : "findings", health, health.Count == 0 ? 0 : 1);
                case "repo hygiene": return Result.Ok(await Repository.Hygiene(root, settings.Output));
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }
    }

    public sealed record GitHubCapability(string Operation, string? TargetRepository, string State, string ProbeKind, string Evidence);
    public sealed record GitHubCapabilities(string Kind, IReadOnlyList<GitHubCapability> Capabilities);

    public interface IGitHubAuthorizationProcess
    {
        Task<ProcessResult> ReadOrigin(string root);
        Task<ProcessResult> ReadShortHead(string root);
        Task<ProcessResult> DryRunPush(string root, string branchRef);
        Task<ProcessResult> AuthStatus(string root);
        Task<ProcessResult> ReadIssues(string root, string repository);
        Task<ProcessResult> ReadRepository(string root, string repository);
        Task<ProcessResult> ReadWorkflowRuns(string root, string repository);
    }

    public sealed class GitHubAuthorizationProcess : IGitHubAuthorizationProcess
    {
        public Task<ProcessResult> ReadOrigin(string root) => GitHubAuthorizationTransport.ReadOrigin(root);
        public Task<ProcessResult> ReadShortHead(string root) => GitHubAuthorizationTransport.ReadShortHead(root);
        public Task<ProcessResult> DryRunPush(string root, string branchRef) => GitHubAuthorizationTransport.DryRunPush(root, branchRef);
        public Task<ProcessResult> AuthStatus(string root) => GitHubAuthorizationTransport.AuthStatus(root);
        public Task<ProcessResult> ReadIssues(string root, string repository) => GitHubAuthorizationTransport.ReadIssues(root, repository);
        public Task<ProcessResult> ReadRepository(string root, string repository) => GitHubAuthorizationTransport.ReadRepository(root, repository);
        public Task<ProcessResult> ReadWorkflowRuns(string root, string repository) => GitHubAuthorizationTransport.ReadWorkflowRuns(root, repository);
    }

    /// <summary>Performs bounded authenticated, read-only GitHub authorization probes for the origin repository.</summary>
    public sealed class GitHubAuthorizationProbe(IGitHubAuthorizationProcess process) : IGitHubAuthorizationProbe
    {
        public async Task<GitHubCapabilities> ProbeAsync(string root, CancellationToken cancellationToken = default)
        {
            ProcessResult remotes;
            try { remotes = await process.ReadOrigin(root); }
            catch { return Unknown(null, "origin could not be read"); }
            var urls = remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            if (remotes.ExitCode != 0 || urls.Length != 1) return Unknown(null, urls.Length == 0 ? "origin is missing" : "origin is ambiguous");
            string target;
            try { var parsed = ParseGitHubTarget(urls[0]); target = $"{parsed.Owner}/{parsed.Repository}"; }
            catch { return Unknown(null, "origin is not a canonical GitHub repository"); }
            ProcessResult auth;
            try { auth = await process.AuthStatus(root); }
            catch { return Unknown(target, "GitHub CLI authentication status unavailable"); }
            var authEvidence = auth.ExitCode == 0 ? auth.Output : "GitHub CLI authentication unavailable";
            ProcessResult issue;
            try { issue = await process.ReadIssues(root, target); }
            catch { issue = new(-1, ""); }
            var read = issue.ExitCode == 0 ? Cap("issues.read", target, "allowed", "authenticated repository issues GET succeeded")
                : ExplicitReject(issue.Output) ? Cap("issues.read", target, "denied", "GitHub explicitly rejected the authenticated issues GET")
                : Cap("issues.read", target, "unknown", "issues GET did not establish access");
            ProcessResult repo;
            try { repo = await process.ReadRepository(root, target); }
            catch { repo = new(-1, ""); }
            var label = LabelCapability(target, repo, auth.ExitCode == 0 ? authEvidence : "");
            var credentialEvidence = auth.ExitCode == 0 ? authEvidence : "";
            var prCreate = PullRequestCreateCapability(target, repo, credentialEvidence);
            var branchPush = await BranchPushCapability(root, target, cancellationToken);
            var workflowRerun = await WorkflowRerunCapability(root, target, repo);
            return new("github-capabilities", [read, label, branchPush, prCreate, workflowRerun]);
        }

        async Task<GitHubCapability> WorkflowRerunCapability(string root, string target, ProcessResult repo)
        {
            ProcessResult actions;
            try { actions = await process.ReadWorkflowRuns(root, target); }
            catch { return Cap("workflow-rerun", target, "unknown", "actions-runs-get", "Actions read evidence unavailable; rerun write permission is unproven"); }
            if (ExplicitReject(actions.Output))
                return Cap("workflow-rerun", target, "denied", "actions-runs-get", "GitHub explicitly denied authenticated Actions access");
            try
            {
                if (repo.ExitCode != 0) throw new InvalidDataException("Repository permissions are unavailable.");
                using var repoDocument = JsonDocument.Parse(repo.Output);
                var permissions = repoDocument.RootElement.GetProperty("permissions");
                var canWrite = permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.True ||
                    permissions.TryGetProperty("admin", out var admin) && admin.ValueKind == JsonValueKind.True;
                if (actions.ExitCode == 0 && canWrite)
                    return Cap("workflow-rerun", target, "allowed", "actions-runs-get", "authenticated Actions read succeeded and repository permissions confirm write access");
            }
            catch { }
            return Cap("workflow-rerun", target, "unknown", "actions-runs-get", actions.ExitCode == 0
                ? "authenticated Actions GET succeeded; it does not prove rerun write permission"
                : "Actions GET did not establish access or rerun write permission");
        }

        async Task<GitHubCapability> BranchPushCapability(string root, string target, CancellationToken cancellationToken)
        {
            ProcessResult head;
            try { head = await process.ReadShortHead(root); }
            catch { return Cap("branch-push", target, "unknown", "current HEAD could not be identified"); }
            var shortHead = head.Output.Trim();
            if (head.ExitCode != 0 || !Regex.IsMatch(shortHead, @"^[0-9a-fA-F]{7,40}$"))
                return Cap("branch-push", target, "unknown", "current HEAD could not be identified");
            cancellationToken.ThrowIfCancellationRequested();
            var branch = $"refs/heads/roadmap/sdeveng-capability-probe-{shortHead}";
            ProcessResult push;
            try { push = await process.DryRunPush(root, branch); }
            catch { return Cap("branch-push", target, "unknown", "dry-run push did not establish authorization"); }
            if (push.ExitCode == 0) return Cap("branch-push", target, "allowed", "git-push-dry-run", "git push dry-run to a unique probe ref succeeded");
            return ExplicitReject(push.Output) || Regex.IsMatch(push.Output, @"(?i)(protected branch|pre-receive hook declined|prohibited by.*policy|repository rule|cannot push|push declined)")
                ? Cap("branch-push", target, "denied", "git-push-dry-run", "remote explicitly rejected the dry-run push")
                : Cap("branch-push", target, "unknown", "git-push-dry-run", "dry-run push failed without a deterministic authorization rejection");
        }

        static GitHubCapability PullRequestCreateCapability(string target, ProcessResult repository, string auth)
        {
            if (repository.ExitCode != 0)
                return ExplicitReject(repository.Output) ? Cap("pr-create", target, "denied", "GitHub explicitly rejected repository permission evidence") : Cap("pr-create", target, "unknown", "repository permission evidence unavailable");
            try
            {
                using var doc = JsonDocument.Parse(repository.Output);
                var permissions = doc.RootElement.GetProperty("permissions");
                var scopes = Regex.Match(auth, @"(?im)Token scopes:\s*(?<scopes>[^\r\n]+)");
                if (permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.False)
                    return Cap("pr-create", target, "denied", "repository permissions explicitly deny push access required by the same-repository pull request workflow");
                if (scopes.Success)
                {
                    var tokenScopes = scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries);
                    if (!tokenScopes.Contains("repo", StringComparer.Ordinal))
                        return Cap("pr-create", target, "denied", "authenticated token scopes explicitly lack the repository scope required for pull request creation");
                    if (permissions.TryGetProperty("push", out push) && push.ValueKind == JsonValueKind.True)
                        return Cap("pr-create", target, "allowed", "repository push permission and authenticated repo token scope are both explicit");
                }
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            return Cap("pr-create", target, "unknown", "available non-mutating evidence does not prove same-repository pull request creation permission");
        }

        static GitHubCapability LabelCapability(string target, ProcessResult repository, string auth)
        {
            if (repository.ExitCode != 0) return ExplicitReject(repository.Output) ? Cap("issues.labels.write", target, "denied", "GitHub explicitly rejected repository permission evidence") : Cap("issues.labels.write", target, "unknown", "repository permission evidence unavailable");
            try
            {
                using var doc = JsonDocument.Parse(repository.Output);
                var permissions = doc.RootElement.GetProperty("permissions");
                if (permissions.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.False)
                    return Cap("issues.labels.write", target, "denied", "repository permissions explicitly deny push access required for issue label edits");
                var scopes = Regex.Match(auth, @"(?im)Token scopes:\s*(?<scopes>[^\r\n]+)");
                var hasRepoScope = scopes.Success && scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries).Contains("repo", StringComparer.Ordinal);
                var hasPublicRepoScope = scopes.Success && scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries).Contains("public_repo", StringComparer.Ordinal) &&
                    doc.RootElement.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.False;
                if (scopes.Success && permissions.TryGetProperty("push", out push) && push.ValueKind == JsonValueKind.True && (hasRepoScope || hasPublicRepoScope))
                    return Cap("issues.labels.write", target, "allowed", "repository push permission and a compatible GitHub token scope are both explicit");
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            return Cap("issues.labels.write", target, "unknown", "available non-mutating evidence does not prove both repository and credential write permission");
        }

        static bool ExplicitReject(string output) => Regex.IsMatch(output, @"(?i)(HTTP\s+401|HTTP\s+403|\b(unauthorized|forbidden|requires authentication|resource not accessible)\b)");
        static GitHubCapability Cap(string operation, string? target, string state, string evidence) => Cap(operation, target, state, "authenticated-get", evidence);
        static GitHubCapability Cap(string operation, string? target, string state, string probeKind, string evidence) => new(operation, target, state, probeKind, evidence);
        static GitHubCapabilities Unknown(string? target, string evidence) => new("github-capabilities", [Cap("issues.read", target, "unknown", evidence), Cap("issues.labels.write", target, "unknown", evidence), Cap("branch-push", target, "unknown", evidence), Cap("pr-create", target, "unknown", evidence), Cap("workflow-rerun", target, "unknown", evidence)]);
        public static (string Owner, string Repository) ParseGitHubTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0 && uri.Port == 443) path = uri.AbsolutePath;
            else if (Uri.TryCreate(remote, UriKind.Absolute, out uri) && uri.Scheme == "ssh" && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo == "git" && uri.Port == 22) path = uri.AbsolutePath;
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a canonical GitHub repository.");
            var parts = path.Trim('/').Split('/');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(parts[1], @"^[A-Za-z0-9_.-]+(?:\.git)?$")) throw new InvalidOperationException("Origin is not a canonical GitHub repository.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
    }

    public sealed class GitHubCommandModule : ICommandModule
    {
        private readonly GitHubPrStatusReader? _prStatusReader;
        private readonly GitHubReviewCommentReader? _reviewCommentReader;
        private readonly GitHubActionsReader? _actionsReader;
        private readonly GitHubAuthorizationProbe? _authorizationProbe;
        public GitHubCommandModule(GitHubPrStatusReader? prStatusReader = null, GitHubReviewCommentReader? reviewCommentReader = null, GitHubActionsReader? actionsReader = null, GitHubAuthorizationProbe? authorizationProbe = null, IGitHubLabelCatalog? labelCatalog = null)
        {
            _prStatusReader = prStatusReader;
            _reviewCommentReader = reviewCommentReader;
            _actionsReader = actionsReader;
            _authorizationProbe = authorizationProbe;
            _labelCatalog = labelCatalog;
        }
        private static (string Owner, string Repository) GitHubRepositoryTarget(string remote)
        {
            string path;
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Origin is not a GitHub repository.");
                path = uri.AbsolutePath;
            }
            else if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote["git@github.com:".Length..];
            else throw new InvalidOperationException("Origin is not a GitHub repository.");
            var parts = path.Trim('/').TrimEnd('/').Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) throw new InvalidOperationException("Origin is not a GitHub repository.");
            return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
        }
        public bool CanHandle(Cli command) => command.Command is "github prepare-pr" or "github pr-status" or "github review-comments" or "github actions" or "github capabilities" or "github labels";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var artifacts = Path.Combine(root, ".agent-tool");
            switch (command.Command)
            {
                case "github capabilities":
                    var probe = _authorizationProbe ?? throw new InvalidOperationException("GitHub authorization probe is unavailable.");
                    return Result.Ok(await probe.ProbeAsync(root, cancellationToken));
                case "github labels":
                    var labelsOrigin = await Git.RemoteUrl(root, "origin");
                    var (labelsOwner, labelsRepository) = GitHubRepositoryTarget(labelsOrigin);
                    var catalogPath = Path.Combine(toolkit, "config", "labels.json");
                    using (var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, cancellationToken)))
                    {
                        var configured = catalog.RootElement.GetProperty("labels").EnumerateArray().Select(item =>
                            new ConfiguredGitHubLabel(
                                item.GetProperty("name").GetString() ?? throw new JsonException("Configured label name is missing."),
                                item.GetProperty("description").GetString() ?? "",
                                item.GetProperty("color").GetString() ?? throw new JsonException("Configured label color is missing."))).ToArray();
                        var api = _labelCatalog ?? throw new InvalidOperationException("GitHub label catalog client is unavailable.");
                        return Result.Ok(await api.SynchronizeAsync($"{labelsOwner}/{labelsRepository}", configured, command.Flag("apply"), cancellationToken));
                    }
                case "github prepare-pr":
                    await Git.EnsureSafe(root, false);
                    var prWhitespace = await Git.WhitespaceCheck(root);
                    return new(prWhitespace.ExitCode != 0 ? "failed" : "ok", new { state = await Git.State(root), whitespace = Output.Compact(prWhitespace.Output, settings.Output), next = "Review explicit file scope before staging. Commit/push/PR creation remains caller-controlled; never merge without approval." }, prWhitespace.ExitCode);
                case "github pr-status":
                    var reader = _prStatusReader ?? throw new InvalidOperationException("GitHub pull request status reader is unavailable.");
                    var branch = await Git.CurrentBranch(root);
                    if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Current checkout is detached; pull request status is unavailable.");
                    var origin = await Git.RemoteUrl(root, "origin");
                    var (owner, repository) = GitHubRepositoryTarget(origin);
                    return Result.Ok(await reader.ReadCurrentBranchAsync(owner, repository, branch, cancellationToken));
                case "github review-comments":
                    var pr = command.PositiveInt("pr").ToString(CultureInfo.InvariantCulture);
                    var reviewReader = _reviewCommentReader ?? throw new InvalidOperationException("GitHub review comment reader is unavailable.");
                    var originUrl = await Git.RemoteUrl(root, "origin");
                    var (reviewOwner, reviewRepository) = GitHubRepositoryTarget(originUrl);
                    var comments = new List<JsonElement>();
                    Uri? page = null;
                    do
                    {
                        var result = await reviewReader.ReadPageAsync(reviewOwner, reviewRepository, int.Parse(pr, CultureInfo.InvariantCulture), page, cancellationToken);
                        comments.AddRange(result.Comments.Select(comment => comment.Raw));
                        page = result.NextPage;
                    } while (page is not null);
                    SafeFiles.NoLinks(artifacts);
                    Directory.CreateDirectory(artifacts);
                    var path = Path.Combine(artifacts, $"github-review-comments-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(comments), cancellationToken);
                    return Result.Ok(new ProcessReport(0, Output.SummarizeFile(path, settings.Output), path));
                case "github actions":
                    var actionsReader = _actionsReader ?? throw new InvalidOperationException("GitHub Actions reader is unavailable.");
                    var actionsOrigin = await Git.RemoteUrl(root, "origin");
                    var (actionsOwner, actionsRepository) = GitHubRepositoryTarget(actionsOrigin);
                    return await GitHub.Actions(actionsReader, actionsOwner, actionsRepository, artifacts, command.Get("run-id"), command.Flag("failed-logs"), settings.Output, cancellationToken);
                default: throw new ArgumentException("Unknown command. Use --help.");
            }
        }
        private readonly IGitHubLabelCatalog? _labelCatalog;
    }

    public sealed class DotnetCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        [
            "dotnet verify", "dotnet format", "dotnet package-audit", "dotnet dependencies",
            "dotnet api-check", "dotnet release-verify", "dotnet inspect", "dotnet build-plan",
            "dotnet test-plan", "dotnet diagnostics-plan", "dotnet semantic-model", "dotnet relationships", "dotnet test-candidates", "dotnet affected-symbols", "dotnet affected-files"
        ];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = command.Command;
            command.ValidateCommand(name);
            var artifacts = Path.Combine(root, ".agent-tool");
            return name switch
            {
                "dotnet verify" or "dotnet format" or "dotnet package-audit" or "dotnet dependencies" or "dotnet api-check" or "dotnet release-verify" => await Dotnet(name, command, root, artifacts, settings),
                "dotnet inspect" => Result.Ok(await DotnetFacts.Inspect(root, command.Get("project"))),
                "dotnet semantic-model" => Result.Ok(await Projects.SemanticModel(root, command.Get("project") ?? throw new ArgumentException("dotnet semantic-model requires --project SOLUTION."))),
                "dotnet relationships" => Result.Ok((await Projects.SemanticModel(root, command.Get("project") ?? throw new ArgumentException("dotnet relationships requires --project SOLUTION.")))
                    .Relationships(command.Get("path") ?? throw new ArgumentException("dotnet relationships requires --path PROJECT."), settings.Output.MaxItems)),
                "dotnet test-candidates" => Result.Ok((await Projects.SemanticModel(root, command.Get("project") ?? throw new ArgumentException("dotnet test-candidates requires --project SOLUTION.")))
                    .TestCandidates(command.Get("symbol") ?? throw new ArgumentException("dotnet test-candidates requires --symbol KEY."), settings.Output.MaxItems)),
                "dotnet affected-symbols" => Result.Ok((await Projects.SemanticModel(root, command.Get("project") ?? throw new ArgumentException("dotnet affected-symbols requires --project SOLUTION.")))
                    .AffectedSymbols(command.Get("symbol") ?? throw new ArgumentException("dotnet affected-symbols requires --symbol KEY."), settings.Output.MaxItems)),
                "dotnet affected-files" => Result.Ok((await Projects.SemanticModel(root, command.Get("project") ?? throw new ArgumentException("dotnet affected-files requires --project SOLUTION.")))
                    .AffectedFiles(command.Get("path") ?? throw new ArgumentException("dotnet affected-files requires --path FILE."), settings.Output.MaxItems)),
                "dotnet build-plan" => Result.Ok(await DotnetFacts.BuildPlan(root, command.Get("project"), command.Get("base"), command.Get("configuration") ?? "Debug", command.Flag("binlog"))),
                "dotnet test-plan" => Result.Ok(await DotnetFacts.TestPlan(root, command.Get("project"), command.Get("base"), command.Get("configuration") ?? "Debug", new(command.Get("test"), command.Get("class"), command.Get("category"), command.Get("filter")))),
                "dotnet diagnostics-plan" => Result.Ok(await DotnetFacts.DiagnosticsPlan(command.Get("process-id"), command.Get("signal"), command.Get("duration-seconds"), root)),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }
    }

    public sealed class ReportCommandModule : ICommandModule
    {
        private static readonly string[] Commands =
        [
            "logs summarize", "sarif summarize", "artifact inspect", "artifact verify",
            "test-results summarize", "coverage summarize"
        ];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            return Task.FromResult(command.Command switch
            {
                "logs summarize" => Result.Ok(Output.SummarizeFile(command.Require("file"), settings.Output)),
                "sarif summarize" => Result.Ok(Output.Sarif(command.Require("file"), settings.Output, command.Get("baseline"))),
                "artifact inspect" => Result.Ok(Artifacts.Inspect(command.Require("file"), settings.Output)),
                "artifact verify" => Artifacts.Verify(command.Require("file"), command.Require("sha256")),
                "test-results summarize" => Result.Ok(DotnetArtifacts.TestResults(command.Require("file"), settings.Output)),
                "coverage summarize" => Result.Ok(DotnetArtifacts.Coverage(command.Require("file"), settings.Output)),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            });
        }
    }

    public sealed class ReleaseValidationCommandModule : ICommandModule
    {
        public bool CanHandle(Cli command) => command.Command == "release evidence";
        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            command.ValidateCommand(command.Command);
            var published = await ReleaseValidationPublisher.PublishAsync(root, command.Require("profile"), command.Get("output") ?? "release-validation-evidence.json", cancellationToken, command.Get("evidence-file"), command.Get("tag"));
            return Result.Ok(new { manifest = published.Manifest, output = Path.GetRelativePath(root, published.Output) });
        }
    }

    public sealed class JevCommandModule : ICommandModule
    {
        private static readonly string[] Commands = ["jev noul", "jev choice", "jev score", "jev screen", "jev cache-clear"];

        public bool CanHandle(Cli command) => Commands.Contains(command.Command, StringComparer.Ordinal);

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            if (command.Command == "jev cache-clear")
            {
                var cachePath = Path.Combine(root, ".agent-tool", "jev-cache");
                SafeFiles.NoLinks(cachePath);
                if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
                return Result.Ok(new { cleared = cachePath });
            }

            return await JevCommand(command, command.Command[4..], root, settings.Jev);
        }
    }

    public sealed class UpstreamCommandModule : ICommandModule
    {
        readonly GitHubCommitReader? _commitReader;
        public UpstreamCommandModule(GitHubCommitReader? commitReader = null) => _commitReader = commitReader;
        public bool CanHandle(Cli command) => command.Command is "upstream status" or "upstream update" or "upstream dotnet-skills";

        public async Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ValidateCommand(command.Command);
            var artifacts = Path.Combine(root, ".agent-tool");
            return command.Command switch
            {
                "upstream status" => Result.Ok(new { plugins = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))), tools = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/tools.json"))), versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json"))) }),
                "upstream update" => await Upstream(toolkit, artifacts, command.Flag("dry-run"), _commitReader),
                "upstream dotnet-skills" => await DotnetSkillsDrift.Run(toolkit, artifacts, command.Words.Skip(2).SingleOrDefault(), command.Flag("dry-run"), _commitReader),
                _ => throw new ArgumentException("Unknown command. Use --help.")
            };
        }
    }

    public sealed class RuntimeSettingsOptions { public Settings Settings { get; set; } = new(new(), new(), new(), new()); public string? ValidationError { get; set; } }

    public static string RenderJson(Result result, string command, string root, OutputSettings output)
    {
        var rendered = SerializeEnvelope(result, command, null);
        if (Encoding.UTF8.GetByteCount(rendered) <= output.MaxOutputChars) return rendered;
        var report = Path.Combine(root, ".agent-tool", $"result-{Guid.NewGuid():N}.json");
        SafeFiles.Atomic(report, rendered);
        return SerializeEnvelope(new(result.Status, new { truncated = true, characters = rendered.Length, artifact = report }, result.ExitCode), command, null);
    }

    public static string SerializeEnvelope(Result result, string command, object? error) => Secrets.RedactJson(JsonSerializer.Serialize(new
    {
        schemaVersion = ResultSchemaVersion,
        product = Product,
        cliVersion = CliVersion,
        command,
        result.Status,
        result.ExitCode,
        data = result.Data,
        error
    }, Json));

    public static string Render(Result result, string root, OutputSettings output)
    {
        var rendered = Secrets.RedactJson(JsonSerializer.Serialize(result, Json));
        if (Encoding.UTF8.GetByteCount(rendered) <= output.MaxOutputChars) return rendered;
        var report = Path.Combine(root, ".agent-tool", $"result-{Guid.NewGuid():N}.json");
        SafeFiles.Atomic(report, rendered);
        return Secrets.RedactJson(JsonSerializer.Serialize(new { result.Status, result.ExitCode, truncated = true, characters = rendered.Length, artifact = report }, Json));
    }

    public static string FindToolkit(string? explicitRoot = null, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var path = explicitRoot ?? Environment.GetEnvironmentVariable("SDEVENG_ROOT") ?? Environment.GetEnvironmentVariable("CODEX_TOOLKIT_ROOT");
        if (path is null)
        {
            if (File.Exists(source)) source = File.ResolveLinkTarget(source, true)?.FullName ?? source;
            for (var parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
            for (var parent = Environment.CurrentDirectory; path is null && parent is not null; parent = Directory.GetParent(parent)?.FullName)
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
        }
        path ??= Environment.CurrentDirectory;
        path = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(path, "config", "toolkit.json"))) throw new ArgumentException("Toolkit root not found; pass --toolkit DIR, SDEVENG_ROOT, or legacy CODEX_TOOLKIT_ROOT.");
        return path;
    }

    public static Task<Result> Execute(Cli c, string toolkit, string root, Settings settings)
    {
        ICommandModule[] modules =
        [
            new InstallerCommandModule(), new DoctorCommandModule(), new GitCommandModule(), new RepoCommandModule(),
            new GitHubCommandModule(), new DotnetCommandModule(), new ReportCommandModule(), new JevCommandModule(),
            new UpstreamCommandModule(), new ValidateCommandModule(), new EvalCommandModule(), new ReleaseCommandModule(), new ReleaseValidationCommandModule(), new RunCommandModule(),
            new ResultsCommandModule()
        ];
        return new AgentToolRuntime(NullLogger<AgentToolRuntime>.Instance, modules)
            .Execute(c, toolkit, root, settings);
    }

    static async Task<Result> Dotnet(string command, Cli c, string root, string artifacts, Settings settings)
    {
        var explicitProject = c.Get("project");
        if (command != "dotnet verify" && explicitProject is null) throw new ArgumentException("This command requires --project PATH (project or solution).");
        var targets = explicitProject is not null ? new[] { Path.GetFullPath(explicitProject, root) } : (await Projects.Affected(root, await Git.Changed(root, c.Get("base")))).Projects.ToArray();
        if (targets.Any(x => !File.Exists(x))) throw new ArgumentException("Project or solution does not exist.");
        var results = new List<Result>();
        foreach (var project in targets)
        {
            if (command == "dotnet format")
            {
                var changed = await Git.Changed(root, c.Get("base"));
                var code = changed.Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root, x))).Select(x => Path.Combine(root, x)).ToArray();
                if (code.Length == 0) continue;
                var args = new List<string> { "format", project, "--include" }; args.AddRange(code);
                if (!c.Flag("apply")) args.Add("--verify-no-changes");
                results.Add(await RunArtifact("dotnet", args, root, artifacts, settings.Output));
            }
            else if (command == "dotnet dependencies")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--include-transitive", "--format", "json", "--output-version", "1"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p) r = Result.Ok(DotnetArtifacts.Dependencies(p.Artifact, root, settings.Output));
                results.Add(r);
            }
            else if (command == "dotnet package-audit")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--vulnerable", "--include-transitive", "--format", "json"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p)
                {
                    var report = JsonNode.Parse(File.ReadAllText(p.Artifact));
                    var vulnerabilities = Audit.Count(report);
                    r = new(vulnerabilities > 0 ? "vulnerable" : "ok", new { vulnerabilities, p.Artifact }, vulnerabilities > 0 ? 1 : 0);
                }
                results.Add(r);
            }
            else if (command == "dotnet api-check")
            {
                if (!await Projects.HasApiChecks(root, project)) throw new InvalidOperationException("Configure PublicApiAnalyzers or EnablePackageValidation with a baseline first; api-check cannot certify an unconfigured project.");
                results.Add(await RunArtifact("dotnet", ["pack", project, "-p:EnablePackageValidation=true", "-p:TreatWarningsAsErrors=true"], root, artifacts, settings.Output));
            }
            else
            {
                var steps = new List<string[]> { new[] { "build", project, "--nologo" } };
                if (command == "dotnet release-verify") steps.Insert(0, ["restore", project, "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"]);
                if (command == "dotnet release-verify") steps.Add(["format", project, "--verify-no-changes"]);
                var isSolution = project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
                if (isSolution || await Projects.IsTest(root, project)) steps.Add(["test", project, "--no-build", "--nologo"]);
                else if (command == "dotnet release-verify")
                {
                    var tests = await Projects.DependentTests(root, project);
                    if (tests.Length == 0) throw new InvalidOperationException("Release verification cannot establish test coverage for this project. Pass a solution or add a dependent test project.");
                    steps.AddRange(tests.Select(test => new[] { "test", test, "--nologo" }));
                }
                foreach (var step in steps)
                {
                    var r = await RunArtifact("dotnet", step, root, artifacts, settings.Output); results.Add(r);
                    if (r.ExitCode != 0) break;
                }
                if (command == "dotnet release-verify" && results.All(r => r.ExitCode == 0))
                    results.Add(await Dotnet("dotnet package-audit", c, root, artifacts, settings));
            }
        }
        return new(results.Any(x => x.ExitCode != 0) ? "failed" : "ok", new { targets, results, scope = command == "dotnet release-verify" ? "restore, build, format, established test coverage, vulnerability audit; API/SBOM/reproducibility gates are separate on-demand skills" : "targeted" }, results.Any(x => x.ExitCode != 0) ? 1 : 0);
    }

    public static async Task<Result> RunArtifact(string executable, IEnumerable<string> args, string root, string artifacts, OutputSettings limits)
    {
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var path = Path.Combine(artifacts, $"{executable}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
        var r = await Processes.Run(executable, args, root, path, TimeSpan.FromMinutes(20));
        return new(r.ExitCode == 0 ? "ok" : "failed", new ProcessReport(r.ExitCode, Output.SummarizeFile(path, limits), path)
        { Verification = VerificationResults.FromLocal(executable, args, r.ExitCode, path) }, r.ExitCode == 0 ? 0 : 1);
    }

    static async Task<Result> JevCommand(Cli c, string kind, string root, JevSettings settings)
    {
        var inputPath = c.Require("input");
        if (Path.GetFileName(inputPath).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return Result.Review("Sensitive input path refused.");
        if (new FileInfo(inputPath).Length > settings.MaxInputBytes) return Result.Review("Input exceeds configured limit.");
        var input = JsonNode.Parse(File.ReadAllText(inputPath)) as JsonObject;
        if (input is null)
        {
            if (kind == "screen") return Result.Review("Screen input must be an object; no candidate discarded.");
            throw new ArgumentException("Input object required.");
        }
        var capability = input["capability"] is JsonValue capabilityValue && capabilityValue.TryGetValue<string>(out var capabilityText) ? capabilityText : "";
        var purpose = input["purpose"] is JsonValue purposeValue && purposeValue.TryGetValue<string>(out var purposeText) ? purposeText : "";
        settings.Capabilities.TryGetValue(capability, out var policy);
        if (policy is null) return JevClient.PolicyReview(null, capability, purpose, "A configured JEV capability is required.");
        if (!policy.Purposes.Contains(purpose, StringComparer.Ordinal)) return JevClient.PolicyReview(policy, capability, purpose, "Purpose is not allowed for this JEV capability.");
        if (!policy.Allowed) return JevClient.PolicyReview(policy, capability, purpose, "JEV is disallowed for this capability; use deterministic tooling or GPT reasoning.");
        var deterministicallyNarrowed = input["deterministicNarrowed"] is JsonValue narrowedValue && narrowedValue.TryGetValue<bool>(out var narrowed) && narrowed;
        if (policy.DeterministicFirst && !deterministicallyNarrowed) return JevClient.PolicyReview(policy, capability, purpose, "Deterministic narrowing is required before JEV.");
        using var session = new JevHttpSession(settings, Path.Combine(root, ".agent-tool/jev-cache"));
        var client = session.Client;
        if (kind == "screen")
        {
            if (input["candidates"] is not JsonArray candidates) return JevClient.PolicyReview(policy, capability, purpose, "Screen candidates array is required; no candidate discarded.");
            var candidateLimit = Math.Min(settings.MaxCandidates, Math.Min(policy.MaxCandidates, policy.MaxCalls));
            if (candidates.Count > candidateLimit) return JevClient.PolicyReview(policy, capability, purpose, "Too many candidates for the capability call budget; narrow deterministic search first.");
            var query = input["query"] is JsonValue queryValue && queryValue.TryGetValue<string>(out var queryText) ? queryText : null;
            if (string.IsNullOrWhiteSpace(query)) return JevClient.PolicyReview(policy, capability, purpose, "Screen query is required; no candidate discarded.");
            var prepared = new List<(string Id, string Text)>();
            foreach (var candidate in candidates)
            {
                if (candidate is not JsonObject item || item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || item["text"] is not JsonValue textValue || !textValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                    return JevClient.PolicyReview(policy, capability, purpose, "Every screen candidate requires a non-empty string id and text; no candidate discarded.");
                if (Secrets.LooksSensitive(id)) return JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected in candidate id; no candidate discarded.");
                prepared.Add((id, text));
            }
            if (prepared.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != prepared.Count)
                return JevClient.PolicyReview(policy, capability, purpose, "Screen candidate ids must be unique; no candidate discarded.");
            var answers = new List<object>(); int exitCode = 0, remoteCalls = 0, cacheHits = 0, fallbacks = 0, escalations = 0, uncertain = 0, contextAvoidedBytes = 0;
            foreach (var candidate in prepared)
            {
                var request = JevClient.Request("noul", candidate.Text, $"Is this candidate relevant to: {query}", null, settings.Model);
                var judgment = Secrets.LooksSensitive(request.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : c.Flag("dry-run") ? Result.Ok(request) : !c.Flag("safe-input") ? JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after minimizing and reviewing supplied text for external transmission.") : await client.Judge(request, policy, purpose, capability);
                exitCode = Math.Max(exitCode, judgment.ExitCode);
                answers.Add(new { id = candidate.Id, judgment });
                if (judgment.Status == "EXCLUDE") contextAvoidedBytes += Encoding.UTF8.GetByteCount(candidate.Text);
                if (judgment.Status == "REVIEW") uncertain++;
                var telemetry = JsonSerializer.SerializeToNode(judgment.Data, Json)?["instrumentation"];
                remoteCalls += telemetry?["counts"]?["remoteCalls"]?.GetValue<int>() ?? 0;
                cacheHits += telemetry?["counts"]?["cacheHits"]?.GetValue<int>() ?? 0;
                fallbacks += telemetry?["counts"]?["fallbacks"]?.GetValue<int>() ?? 0;
                escalations += telemetry?["counts"]?["escalations"]?.GetValue<int>() ?? 0;
            }
            return new(exitCode == 0 ? "ok" : "REVIEW", new
            {
                judgments = answers,
                instrumentation = new
                {
                    schemaVersion = 1,
                    capability,
                    purpose,
                    privacy = policy.Privacy,
                    budget = new { expectedCalls = policy.ExpectedCalls, maxCalls = policy.MaxCalls },
                    bounds = new { policy.DeterministicFirst, policy.MaxInputBytes, policy.MaxCandidates },
                    counts = new { candidates = prepared.Count, judgments = c.Flag("dry-run") ? 0 : prepared.Count, remoteCalls, cacheHits, fallbacks, escalations },
                    confidence = new { reported = (double?)null, minimum = policy.MinConfidence, uncertain },
                    fallback = new { used = fallbacks > 0, target = fallbacks > 0 ? "GPT" : null },
                    escalation = new { required = escalations > 0, target = escalations > 0 ? policy.GptEscalation + "-gpt" : null },
                    contextAvoidedBytes,
                    payloadCaptured = false
                }
            }, exitCode);
        }
        if (policy.MaxCalls < 1) return JevClient.PolicyReview(policy, capability, purpose, "JEV call budget is zero for this capability.");
        var payload = JevClient.Request(kind, input["state"]?.GetValue<string>() ?? "", input["instructions"]?.GetValue<string>() ?? "", input["criteria"], settings.Model);
        if (c.Flag("dry-run")) return Secrets.LooksSensitive(payload.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : Result.Ok(payload);
        if (!c.Flag("safe-input")) return JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after reviewing and minimizing supplied text for external transmission.");
        return await client.Judge(payload, policy, purpose, capability);
    }

    static async Task<Result> Upstream(string toolkit, string artifacts, bool dryRun, GitHubCommitReader? commitReader)
    {
        var versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json")))!["repositories"]!.AsArray();
        var rows = new List<object>();
        foreach (var entry in versions)
        {
            var repo = entry!["repository"]!.GetValue<string>(); var pinned = entry["revision"]?.GetValue<string>();
            if (dryRun) { rows.Add(new { repo, pinned, query = $"gh api repos/{repo}/commits/HEAD --jq .sha" }); continue; }
            try
            {
                var parts = repo.Split('/', 2);
                if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
                var latest = (await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).ReadHeadAsync(parts[0], parts[1])).Sha;
                rows.Add(new { repo, pinned, latest, status = latest == pinned ? "current" : "review-update" });
            }
            catch (GitHubTransportException) { rows.Add(new { repo, pinned, latest = (string?)null, status = "unavailable" }); }
        }
        if (dryRun) return Result.Ok(rows);
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "upstream-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(rows, Json));
        return Result.Ok(new { rows, report, policy = "Report only; no downloads, manifest edits or merges." });
    }

    static Result Release(string toolkit, string output)
    {
        var valid = Validation.Run(toolkit); if (valid.ExitCode != 0) return valid;
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var zip = System.IO.Compression.ZipFile.Open(output, System.IO.Compression.ZipArchiveMode.Create);
        var roots = new[] { "agents", "config", "docs", "evals", "global", "plugins", "schemas", "src", "templates", "tools", "upstream" };
        foreach (var file in roots.SelectMany(x => SafeFiles.Enumerate(Path.Combine(toolkit, x))).Concat(new[] { "README.md", "CHANGELOG.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.md", "global.json", "Directory.Build.props", "Directory.Packages.props", "SdevEng.slnx", ".agents/plugins/marketplace.json" }.Select(x => Path.Combine(toolkit, x))))
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, Path.GetRelativePath(toolkit, file).Replace('\\', '/'));
        return Result.Ok(new { archive = output });
    }
}

public static class VerificationResults
{
    public static VerificationDecision Evaluate(string check, IEnumerable<VerificationResult> evidence, VerificationEvidencePolicy policy)
        => VerificationDecisions.Evaluate(check, evidence, policy);

    public static VerificationResult FromLocal(string executable, IEnumerable<string> arguments, int exitCode, string artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact);
        var commandArguments = arguments.ToArray();
        var check = executable == "dotnet" && commandArguments.Length > 0
            ? commandArguments[0] switch
            {
                "build" => "build",
                "test" => "tests",
                "format" => "format",
                "pack" => "package",
                _ => string.Join(" ", new[] { executable }.Concat(commandArguments))
            }
            : string.Join(" ", new[] { executable }.Concat(commandArguments));
        if (check.Length > 512) throw new ArgumentException("Verification check exceeds 512 characters.", nameof(arguments));
        if (exitCode < 0) throw new ArgumentOutOfRangeException(nameof(exitCode));
        return new(2, "local", check, exitCode == 0 ? "passed" : exitCode == 124 ? "timed-out" : "failed", exitCode, Path.GetFullPath(artifact))
        { EnvironmentIdentity = Environment.MachineName };
    }

    public static VerificationResult FromGitHubCheck(GitHubCheck check, string owner, string repository, string commitSha)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (!System.Text.RegularExpressions.Regex.IsMatch(commitSha ?? "", "^(?:[0-9a-f]{40}|[0-9a-f]{64})$"))
            throw new ArgumentException("A full lowercase commit SHA is required.", nameof(commitSha));
        if (check.Name.Length is < 1 or > 512 || check.DetailsUrl is null || check.DetailsUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A named check with an HTTPS details URL is required.", nameof(check));
        if (check.Status != "completed" || check.Conclusion is not ("success" or "failure" or "timed_out" or "cancelled" or "action_required"))
            throw new ArgumentException("Only completed checks with a definitive conclusion can be mapped.", nameof(check));
        var status = check.Conclusion switch { "success" => "passed", "timed_out" => "timed-out", "cancelled" => "cancelled", _ => "failed" };
        return new(2, "hosted", check.Name, status, status == "passed" ? 0 : 1, check.DetailsUrl.AbsoluteUri)
        { EnvironmentIdentity = $"github:{owner}/{repository}@{commitSha}" };
    }
}

public static class DotnetSkillsDrift
{
    // This compares only public Git metadata. It deliberately never checks out, runs, or imports upstream files.
    public static async Task<Result> Run(string toolkit, string artifacts, string? operation, bool dryRun, GitHubCommitReader? commitReader = null)
    {
        if (operation is not ("status" or "diff" or "check")) throw new ArgumentException("Usage: upstream dotnet-skills <status|diff|check> [--dry-run].");
        var manifest = FrameworkProvenance.Read(JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))) ?? throw new FormatException("Dotnet skills provenance manifest is empty."));
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Dotnet skills provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        if (operation == "status")
        {
            if (dryRun) return Result.Ok(new { kind = "dotnet-skills-status", repository, pinned, query = $"gh api repos/{repository}/commits/HEAD --jq .sha", policy = "Public metadata only; no source is downloaded or executed." });
            try
            {
                var parts = repository.Split('/', 2);
                if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
                var latest = (await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).ReadHeadAsync(parts[0], parts[1])).Sha;
                return Result.Ok(new { kind = "dotnet-skills-status", repository, pinned, latest, status = latest == pinned ? "current" : "review-update", policy = "Public metadata only; no source is downloaded or executed." });
            }
            catch (GitHubTransportException) { return new("unavailable", new { kind = "dotnet-skills-status", repository, pinned, latest = (string?)null, status = "review-update", policy = "Public metadata only; no source is downloaded or executed." }, 1); }
        }
        if (dryRun) return Result.Ok(new { kind = "dotnet-skills-diff", repository, pinned, query = $"gh api repos/{repository}/compare/{pinned}...HEAD", policy = "Only changed decision paths are reported; no upstream code is executed, merged, or copied." });
        JsonObject authoritative;
        try
        {
            var parts = repository.Split('/', 2);
            if (parts.Length != 2) throw new FormatException("Upstream repository must be owner/repository.");
            var compare = await (commitReader ?? throw new InvalidOperationException("GitHub commit reader is unavailable.")).CompareToHeadAsync(parts[0], parts[1], pinned);
            authoritative = new JsonObject { ["head_commit"] = new JsonObject { ["sha"] = compare.HeadCommitSha }, ["files"] = new JsonArray(compare.Files.Select(file => (JsonNode?)new JsonObject { ["filename"] = file.Filename, ["status"] = file.Status, ["previous_filename"] = file.PreviousFilename }).ToArray()) };
        }
        catch (GitHubTransportException) { return new("unavailable", new { kind = "dotnet-skills-diff", repository, pinned, policy = "Comparison unavailable; no source was downloaded or executed." }, 1); }
        var analysis = Analyze(manifest, authoritative);
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "dotnet-skills-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(analysis, AgentTool.Json));
        var review = analysis["classification"]?.GetValue<string>() is "relevant" or "review-required";
        return new(review && operation == "check" ? "review-required" : "ok", new { kind = "dotnet-skills-drift", analysis, report, policy = "Report only. Review listed public paths before any separate, explicit integration change." }, review && operation == "check" ? 1 : 0);
    }

    public static JsonObject Analyze(JsonNode manifest, JsonNode authoritative)
    {
        manifest = FrameworkProvenance.Read(manifest);
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        var files = authoritative["files"]?.AsArray() ?? throw new FormatException("Authoritative comparison has no files array.");
        var paths = manifest["decisions"]?.AsArray().SelectMany(d => d?["upstreamPaths"]?.AsArray() ?? throw new FormatException("Decision has no upstreamPaths.")).Select(p => p?.GetValue<string>() ?? throw new FormatException("Decision path is invalid.")).ToHashSet(StringComparer.Ordinal) ?? throw new FormatException("Provenance decisions are missing.");
        var relevant = new JsonArray(); var irrelevant = 0;
        foreach (var item in files)
        {
            var file = item?.AsObject() ?? throw new FormatException("Authoritative comparison file is invalid.");
            var path = Required(file, "filename"); var status = Required(file, "status");
            if (status is not ("added" or "modified" or "removed" or "renamed")) throw new FormatException("Authoritative comparison has an unknown file status.");
            var previous = file["previous_filename"]?.GetValue<string>();
            if (!paths.Contains(path) && (previous is null || !paths.Contains(previous))) { irrelevant++; continue; }
            var disposition = status is "removed" or "renamed" ? "review-required" : "relevant";
            relevant.Add(new JsonObject { ["path"] = path, ["status"] = status, ["previousPath"] = previous, ["classification"] = disposition, ["inspect"] = "Fetch this one public path only if a maintainer needs its diff." });
        }
        var classification = relevant.Count == 0 ? (files.Count == 0 ? "no-change" : "irrelevant") : relevant.Any(f => f!["classification"]!.GetValue<string>() == "review-required") ? "review-required" : "relevant";
        return new JsonObject { ["schemaVersion"] = 1, ["kind"] = "dotnet-skills-drift", ["repository"] = repository, ["pinned"] = pinned, ["authoritativeHead"] = authoritative["head_commit"]?["sha"]?.GetValue<string>(), ["classification"] = classification, ["changedFiles"] = files.Count, ["relevant"] = relevant, ["irrelevantCount"] = irrelevant, ["automaticAction"] = "none" };
    }

    static string Required(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 } text ? text : throw new FormatException($"Missing {name}.");
}

public sealed class Cli
{
    public string Command => Words.FirstOrDefault() is "results" ? string.Join(' ', Words.Take(2))
        : Words.FirstOrDefault() == "upstream" && Words.ElementAtOrDefault(1) == "dotnet-skills" ? string.Join(' ', Words.Take(2))
        : Words.FirstOrDefault() == "run" ? string.Join(' ', Words.Take(2))
        : string.Join(' ', Words);

    public void ValidateCommand(string command)
    {
        var allowed = new HashSet<string>(new[] { "root", "toolkit", "set", "json", "help", "version" });
        string[] specific = command switch
        {
            "config explain" => ["set", "role", "measurements", "model-context"],
            "skills explain" => ["input"],
            "install" or "update" => ["home", "codex-home", "dry-run", "bin"],
            "uninstall" => ["home", "codex-home", "dry-run"],
            "doctor" => ["home", "codex-home"],
            "repo changed-files" or "repo affected-projects" or "repo summary" or "git summary" or "git conflict-forecast" => ["base"],
            "repo locate" => ["query", "path", "name", "exact-text", "fuzzy"],
            "repo ownership" => ["file"],
            "git issue-start" => ["issue", "branch"],
            "git stage-owned" => ["paths-file"],
            "git commit-owned" => ["paths-file", "message"],
            "git branch-create" => ["branch"],
            "git worktree-create" => ["branch", "path"],
            "git push-owned" => ["remote", "branch"],
            "git worktree-remove-owned" => ["path"],
            "git stale-base" => ["base", "expected"],
            "git abandon-owned" => ["branch", "path"],
            "github review-comments" => ["pr"],
            "github labels" => ["apply", "dry-run"],
            "github actions" => ["run-id", "failed-logs"],
            "dotnet verify" => ["base", "project"],
            "dotnet inspect" => ["project"],
            "dotnet semantic-model" => ["project"],
            "dotnet relationships" => ["project", "path"],
            "dotnet test-candidates" => ["project", "symbol"],
            "dotnet affected-symbols" => ["project", "symbol"],
            "dotnet affected-files" => ["project", "path"],
            "dotnet build-plan" => ["base", "project", "configuration", "binlog"],
            "dotnet test-plan" => ["base", "project", "configuration", "test", "class", "category", "filter"],
            "dotnet diagnostics-plan" => ["process-id", "signal", "duration-seconds"],
            "dotnet format" => ["base", "project", "apply"],
            "dotnet dependencies" or "dotnet package-audit" or "dotnet api-check" or "dotnet release-verify" => ["project"],
            "logs summarize" or "test-results summarize" or "coverage summarize" or "artifact inspect" => ["file"],
            "sarif summarize" => ["file", "baseline"],
            "artifact verify" => ["file", "sha256"],
            "jev noul" or "jev choice" or "jev score" or "jev screen" => ["input", "dry-run", "safe-input"],
            "upstream update" or "upstream dotnet-skills" => ["dry-run"],
            "eval" => ["skill", "results"],
            "release" => ["output"],
            "release evidence" => ["profile", "output", "evidence-file", "tag"],
            "verification decide" => ["hosted-file", "local-file", "commit"],
            "results clean" => ["dry-run"],
            _ => []
        };
        allowed.UnionWith(specific);
        foreach (var option in Options.Keys)
            if (!allowed.Contains(option)) throw new ArgumentException($"--{option} is not supported by this command.");
        if (command == "github labels" && Flag("apply") && Flag("dry-run"))
            throw new ArgumentException("github labels accepts either --dry-run or --apply, not both.");
    }
    public List<string> Words { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);
    static readonly HashSet<string> Flags = ["json", "help", "version", "dry-run", "bin", "apply", "safe-input", "binlog", "failed-logs"];
    static readonly HashSet<string> Values = ["role", "measurements", "model-context", "root", "toolkit", "set", "home", "codex-home", "base", "expected", "baseline", "query", "exact-text", "fuzzy", "name", "issue", "branch", "remote", "path", "paths-file", "message", "pr", "run-id", "project", "symbol", "file", "sha256", "input", "output", "skill", "results", "configuration", "test", "class", "category", "filter", "process-id", "signal", "duration-seconds", "profile", "evidence-file", "tag", "hosted-file", "local-file", "commit"];
    public string? Get(string name) => Options.GetValueOrDefault(name);
    public bool Flag(string name) => Options.ContainsKey(name);
    public string Require(string name) => Get(name) is { Length: > 0 } v ? v : throw new ArgumentException($"--{name} is required.");
    public IEnumerable<KeyValuePair<string, string?>> ConfigurationOverrides()
    {
        var allowed = new HashSet<string>(["JEV_MODE", "TYPESAFE_API_URL", "JEV_MODEL", "JEV_TIMEOUT_SECONDS"], StringComparer.Ordinal);
        foreach (var entry in (Options.GetValueOrDefault("set") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2 || !allowed.Contains(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
                throw new ArgumentException("--set requires NAME=VALUE for a supported setting: JEV_MODE, TYPESAFE_API_URL, JEV_MODEL, or JEV_TIMEOUT_SECONDS.");
            yield return new(pair[0], pair[1]);
        }
    }
    public int PositiveInt(string name) => int.TryParse(Require(name), out var i) && i > 0 ? i : throw new ArgumentException($"--{name} must be a positive integer.");
    public static Cli Parse(string[] args)
    {
        var result = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-h") arg = "--help";
            if (arg == "-V") arg = "--version";
            if (!arg.StartsWith('-')) { result.Words.Add(arg); continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Only long options are supported.");
            var parts = arg[2..].Split('=', 2); var name = parts[0];
            if (!Flags.Contains(name) && !Values.Contains(name)) throw new ArgumentException($"Unknown option --{name}.");
            if (result.Options.ContainsKey(name) && name != "set") throw new ArgumentException($"Duplicate --{name}.");
            if (Flags.Contains(name)) { if (parts.Length != 1) throw new ArgumentException($"--{name} takes no value."); result.Options[name] = null; }
            else
            {
                var value = parts.Length == 2 ? parts[1] : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException($"Missing value for --{name}.");
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Empty --{name}.");
                result.Options[name] = name == "set" && result.Options.TryGetValue(name, out var previous) ? previous + "\0" + value : value;
            }
        }
        return result;
    }
}

public record Settings(JevSettings Jev, OutputSettings Output, HealthSettings Health, ToolkitSettings Toolkit)
{
    public static void AddConfigurationSources(IConfigurationManager configuration, string toolkit)
    {
        foreach (var name in new[] { "jev", "output-limits", "repo-health", "toolkit" })
            configuration.AddJsonFile(Path.Combine(toolkit, "config", name + ".json"), optional: false, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
    }
    public static void RegisterOptions(IServiceCollection services, IConfiguration configuration, string toolkit, string? root = null)
    {
        services.AddOptions<AgentTool.RuntimeSettingsOptions>()
            .Configure(options => { try { options.Settings = Load(toolkit, name => configuration[name], root); } catch (ArgumentException e) { options.ValidationError = e.Message; } })
            .Validate(options => options.ValidationError is null, "Invalid toolkit settings.")
            .Validate(options => options.Settings.Jev.Mode is "off" or "auto" or "required", "Invalid JEV settings.")
            .Validate(options => options.Settings.Output.MaxLines is >= 1 and <= 100 && options.Settings.Output.MaxLineLength is >= 20 and <= 2000 && options.Settings.Output.MaxItems is >= 1 and <= 200 && options.Settings.Output.MaxOutputChars is >= 1024 and <= 131072, "Invalid output settings.")
            .ValidateOnStart();
    }

    public static string MachineDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "sdeveng")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/sdeveng" : "/etc/sdeveng";

    public static string UserDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdeveng")
        : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "sdeveng")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "sdeveng");

    public static IReadOnlyList<string> SourceFiles(string toolkit, string root, string? machineDirectory = null, string? userDirectory = null)
    {
        var paths = new List<string>();
        foreach (var name in new[] { "jev", "output-limits", "repo-health", "toolkit" })
            foreach (var directory in new[] { Path.Combine(toolkit, "config"), machineDirectory ?? MachineDirectory(), userDirectory ?? UserDirectory(), Path.Combine(root, ".sdeveng") })
            {
                var path = Path.Combine(directory, name + ".json");
                if (File.Exists(path)) paths.Add(path);
            }
        return paths;
    }

    public static Settings Load(string toolkit, Func<string, string?>? env = null, string? root = null, string? machineDirectory = null, string? userDirectory = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        root ??= Environment.CurrentDirectory;
        T Read<T>(string name) where T : new()
        {
            var node = new JsonObject();
            var sources = new List<string>();
            foreach (var directory in new[] { Path.Combine(toolkit, "config"), machineDirectory ?? MachineDirectory(), userDirectory ?? UserDirectory(), Path.Combine(root, ".sdeveng") })
            {
                var path = Path.Combine(directory, name + ".json");
                if (!File.Exists(path))
                {
                    if (directory == Path.Combine(toolkit, "config")) throw new ArgumentException($"Missing built-in configuration: {path}");
                    continue;
                }
                try
                {
                    var layer = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("Root must be an object.");
                    var payload = (JsonObject)layer.DeepClone();
                    payload.Remove("$schema");
                    var supplied = payload.Deserialize<T>(AgentTool.Json) ?? throw new JsonException("Configuration layer is empty.");
                    if (supplied is JevSettings jevLayer) jevLayer.Validate();
                    if (supplied is OutputSettings outputLayer && (outputLayer.MaxLines is < 1 or > 100 || outputLayer.MaxLineLength is < 20 or > 2000 || outputLayer.MaxItems is < 1 or > 200 || outputLayer.MaxOutputChars is < 1024 or > 131072))
                        throw new ArgumentException("Output limits are out of range.");
                    foreach (var entry in layer)
                        if (entry.Key != "$schema") node[entry.Key] = entry.Value?.DeepClone();
                    sources.Add(path);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                { throw new ArgumentException($"Invalid configuration in {path}: {exception.Message}", exception); }
            }
            try { return node.Deserialize<T>(AgentTool.Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new ArgumentException($"Invalid merged configuration from {string.Join(", ", sources)}."); }
        }
        var jev = Read<JevSettings>("jev");
        jev = jev with { Mode = env("JEV_MODE") ?? jev.Mode, ApiUrl = env("TYPESAFE_API_URL") ?? jev.ApiUrl, Model = env("JEV_MODEL") ?? jev.Model, TimeoutSeconds = env("JEV_TIMEOUT_SECONDS") is { } timeout ? int.TryParse(timeout, out var seconds) ? seconds : throw new ArgumentException("Invalid JEV_TIMEOUT_SECONDS.") : jev.TimeoutSeconds };
        try { jev.Validate(); }
        catch (ArgumentException exception) { throw new ArgumentException("Invalid effective JEV configuration after environment/invocation overrides: " + exception.Message, exception); }
        var output = Read<OutputSettings>("output-limits");
        if (output.MaxLines is < 1 or > 100 || output.MaxLineLength is < 20 or > 2000 || output.MaxItems is < 1 or > 200 || output.MaxOutputChars is < 1024 or > 131072) throw new ArgumentException("Invalid output limits.");
        return new(jev, output, Read<HealthSettings>("repo-health"), Read<ToolkitSettings>("toolkit"));
    }
    public static Settings LoadFor(string toolkit, string command, Settings configured)
        => command is "install" or "update" or "uninstall" or "validate" or "release" or "results init" or "results new" or "results list" or "results latest" or "results context" or "results clean" or "upstream status" or "upstream update" or "upstream dotnet-skills"
            ? new(new(), new(), new(), new()) : configured;
}

public record InstallEntry(string Destination, string Source, bool Directory);
public record InstallManifest(string Toolkit, string Home, string CodexHome, List<InstallEntry> Entries);
public static class Installer
{
    static string ManifestPath(string codex) => Path.Combine(codex, "sdeveng-install.json");
    static string LegacyManifestPath(string codex) => Path.Combine(codex, "codex-toolkit-install.json");
    static string? LinkTarget(InstallEntry entry)
    {
        FileSystemInfo info = entry.Directory ? new DirectoryInfo(entry.Destination) : new FileInfo(entry.Destination);
        try
        {
            if (info.ResolveLinkTarget(false) is { } resolved) return resolved.FullName;
        }
        catch (IOException) { }
        var target = info.LinkTarget;
        return target is null ? null : Path.GetFullPath(target, Path.GetDirectoryName(entry.Destination)!);
    }
    static bool Exists(InstallEntry entry) => File.Exists(entry.Destination) || Directory.Exists(entry.Destination) || LinkTarget(entry) is not null;
    static bool Matches(InstallEntry e)
    {
        var target = LinkTarget(e);
        return target is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(e.Source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    static void DeleteLink(InstallEntry entry)
    {
        if (entry.Directory && (OperatingSystem.IsWindows() || Directory.Exists(entry.Destination))) Directory.Delete(entry.Destination);
        else File.Delete(entry.Destination);
    }
    static List<InstallEntry> Plan(string toolkit, string home, string codex, bool bin)
    {
        if (bin && OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--bin is not supported on Windows; invoke `dotnet <toolkit>/tools/AgentTool.cs` directly.");
        var entries = new List<InstallEntry> { new(Path.Combine(codex, "AGENTS.md"), Path.Combine(toolkit, "global/AGENTS.md"), false) };
        entries.AddRange(Directory.GetFiles(Path.Combine(toolkit, "agents"), "*.toml").Select(s => new InstallEntry(Path.Combine(codex, "agents", Path.GetFileName(s)), s, false)));
        entries.AddRange(Directory.GetDirectories(Path.Combine(toolkit, "plugins/sdeveng/skills")).Select(s => new InstallEntry(Path.Combine(home, ".agents/skills", Path.GetFileName(s)), s, true)));
        if (bin)
        {
            entries.Add(new(Path.Combine(home, ".local/bin/sdeveng"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
            entries.Add(new(Path.Combine(home, ".local/bin/codex-agent-tool"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
        }
        return entries;
    }
    static bool IsOwnedShape(InstallEntry entry, string toolkit, string home, string codex)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static bool Under(string path, string root, StringComparison comparison) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
        var destination = Path.GetFullPath(entry.Destination); var source = Path.GetFullPath(entry.Source);
        if (!Under(source, toolkit, comparison)) return false;
        return destination == Path.Combine(codex, "AGENTS.md") && source == Path.Combine(toolkit, "global", "AGENTS.md") && !entry.Directory
            || Under(destination, Path.Combine(codex, "agents"), comparison) && destination.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && !entry.Directory
            || Under(destination, Path.Combine(home, ".agents", "skills"), comparison) && entry.Directory
            || (destination == Path.Combine(home, ".local", "bin", "sdeveng") || destination == Path.Combine(home, ".local", "bin", "codex-agent-tool")) && (source == Path.Combine(toolkit, "tools", "AgentTool.cs") || source == Path.Combine(toolkit, "tools", "sdeveng-launcher")) && !entry.Directory;
    }
    static InstallManifest? Read(string path)
    {
        SafeFiles.NoLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), AgentTool.Json) ?? throw new IOException("Invalid installation manifest.") : null;
    }
    public static object Inspect(string codex)
    {
        var manifest = Read(ManifestPath(codex));
        var legacy = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        return manifest is null && legacy is null ? new { installed = false } : new { installed = true, migrationRequired = legacy is not null, entries = (manifest ?? legacy)!.Entries.Select(e => new { e.Destination, healthy = Matches(e) && (File.Exists(e.Source) || Directory.Exists(e.Source)) }) };
    }
    public static Result Run(string toolkit, string home, string? codexHome, string command, bool dryRun, bool bin)
    {
        toolkit = Path.GetFullPath(toolkit); home = Path.GetFullPath(home); var codex = Path.GetFullPath(codexHome ?? Path.Combine(home, ".codex"));
        SafeFiles.NoLinks(codex);
        var manifestPath = ManifestPath(codex);
        var manifest = Read(manifestPath);
        var legacyManifest = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        var migratingLegacy = legacyManifest is not null;
        manifest ??= legacyManifest;
        if (manifest is not null && (manifest.Toolkit != toolkit || manifest.Home != home || manifest.CodexHome != codex)) throw new IOException("Installation belongs to a different checkout/home; use that checkout to uninstall first.");
        if (manifest is not null && manifest.Entries.Any(e => !IsOwnedShape(e, toolkit, home, codex))) throw new IOException("Ownership manifest contains unexpected paths; no changes made.");
        var plan = command == "uninstall" ? [] : Plan(toolkit, home, codex, bin || manifest?.Entries.Any(x => x.Destination == Path.Combine(home, ".local/bin/codex-agent-tool")) == true);
        var removals = command == "uninstall" ? manifest?.Entries.ToList() ?? [] : command == "update" || migratingLegacy ? manifest?.Entries.Except(plan).ToList() ?? [] : [];
        var conflicts = plan.Where(e => Exists(e) && !(manifest?.Entries.Any(existing => existing.Destination == e.Destination && Matches(existing)) == true)).Select(e => e.Destination).ToArray();
        if (command != "uninstall" && conflicts.Length > 0) return new("conflict", new { conflicts, changed = false }, 1);
        foreach (var entry in plan.Concat(removals)) SafeFiles.NoLinks(Path.GetDirectoryName(entry.Destination)!);
        var preservedRemovals = removals.Where(e => Exists(e) && !Matches(e)).Select(e => e.Destination).ToArray();
        if (dryRun) return Result.Ok(new { dryRun, command, migratingLegacy, plan, removals, preserved = conflicts.Concat(preservedRemovals) });
        if (plan.Count == 0 && removals.Count == 0) return Result.Ok(new { command, changed = 0 });
        Directory.CreateDirectory(codex);
        var lockPath = Path.Combine(codex, "sdeveng-install.lock"); SafeFiles.NoLinks(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        // Re-read under the lock so stale concurrent plans cannot overwrite ownership records.
        var current = Read(migratingLegacy ? LegacyManifestPath(codex) : manifestPath);
        if (JsonSerializer.Serialize(current, AgentTool.Json) != JsonSerializer.Serialize(manifest, AgentTool.Json)) throw new IOException("Installation changed concurrently; rerun command.");
        manifest ??= new(toolkit, home, codex, []);
        var changed = new List<string>(); var preserved = new List<string>();
        foreach (var entry in removals)
        {
            if (Matches(entry)) { DeleteLink(entry); changed.Add(entry.Destination); }
            else if (Exists(entry)) preserved.Add(entry.Destination);
            manifest.Entries.Remove(entry);
            SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json));
        }
        foreach (var entry in plan)
        {
            if (manifest.Entries.Contains(entry) && Matches(entry)) continue;
            if (Exists(entry)) throw new IOException("Destination appeared during installation; rerun to inspect conflicts.");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
            if (entry.Directory) Directory.CreateSymbolicLink(entry.Destination, entry.Source); else File.CreateSymbolicLink(entry.Destination, entry.Source);
            manifest.Entries.Remove(entry);
            manifest.Entries.Add(entry);
            try { SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json)); }
            catch { if (Matches(entry)) DeleteLink(entry); throw; }
            changed.Add(entry.Destination);
        }
        if (migratingLegacy || command == "uninstall") File.Delete(LegacyManifestPath(codex));
        if (command == "uninstall") File.Delete(manifestPath);
        return Result.Ok(new { command, migratingLegacy, changed, preserved, note = "Only owned links changed; user replacements are preserved. Empty parent directories remain." });
    }
}

public static class RuntimeReferences
{
    static readonly Regex MarkdownLink = new(@"\]\(([^)]+)\)", RegexOptions.Compiled);
    static readonly Regex SkillReference = new(@"(?<![A-Za-z0-9_./-])(references/[A-Za-z0-9_./-]+\.md)\b", RegexOptions.Compiled);

    public static string[] Missing(string root)
    {
        var plugin = Path.Combine(root, "plugins", "sdeveng");
        var skills = Path.Combine(plugin, "skills");
        if (!Directory.Exists(skills)) return ["Missing runtime skills directory: plugins/sdeveng/skills"];
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var markdown = SafeFiles.Enumerate(plugin).Where(file => file.EndsWith(".md", StringComparison.Ordinal)
            && (Path.GetFileName(file) == "SKILL.md" || file.Contains(Path.DirectorySeparatorChar + "references" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        foreach (var file in markdown)
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "SKILL.md")
                foreach (Match match in SkillReference.Matches(text)) Check(file, match.Groups[1].Value, root, errors);
            foreach (Match match in MarkdownLink.Matches(text))
            {
                var target = match.Groups[1].Value.Split('#')[0].Trim('<', '>');
                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                Check(file, target, root, errors);
            }
        }
        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    static void Check(string source, string target, string root, HashSet<string> errors)
    {
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(source)!);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            errors.Add($"Missing runtime reference from {Path.GetRelativePath(root, source)}: {target}");
    }
}

public static class PluginManifests
{
    public const string PortableSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    public const string PluginName = "sdeveng-engineering-toolkit";

    public static JsonObject CompatibilityFrom(JsonObject portable) => new()
    {
        ["name"] = portable["name"]?.DeepClone(),
        ["version"] = portable["version"]?.DeepClone(),
        ["description"] = portable["description"]?.DeepClone(),
        ["skills"] = "./skills/",
        ["interface"] = portable["extensions"]?["com.openai"]?["interface"]?.DeepClone()
    };

    public static void Validate(string root, List<string> errors)
    {
        var pluginRoot = Path.Combine(root, "plugins", "sdeveng");
        try
        {
            var index = SkillCompatibilityMapReader.ReadMetadataIndex(root);
            SkillCompatibilityMapReader.ValidateRequiredTools(root, index, errors);
            SkillCompatibilityMapReader.ValidateResources(root, index);
            var portable = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, "plugin.json"))) as JsonObject;
            var compatibility = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, ".codex-plugin", "plugin.json"))) as JsonObject;
            if (portable is null || compatibility is null) { errors.Add("Plugin manifests must be JSON objects."); return; }
            if (portable["$schema"]?.GetValue<string>() != PortableSchema || portable["name"]?.GetValue<string>() != PluginName)
                errors.Add("Portable plugin schema or stable identity mismatch.");
            if (portable["author"]?["name"]?.GetValue<string>() != "SimplexiDev Engineering Toolkit"
                || portable["author"]?["url"]?.GetValue<string>() != "https://github.com/simplexidev"
                || portable["homepage"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng"
                || portable["repository"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng")
                errors.Add("Portable plugin publisher metadata mismatch.");
            if (portable["extensions"]?["com.openai"]?["interface"] is not JsonObject)
                errors.Add("Portable plugin OpenAI extension metadata missing.");
            if (!JsonNode.DeepEquals(compatibility, CompatibilityFrom(portable)))
                errors.Add("Codex compatibility manifest must be derived from the portable manifest.");
            if (!Directory.Exists(Path.Combine(pluginRoot, "skills")) || !Directory.GetDirectories(Path.Combine(pluginRoot, "skills")).Any())
                errors.Add("Portable plugin skill discovery directory is missing or empty.");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            errors.Add("Unable to validate plugin manifests: " + e.Message);
        }
    }
}

public static class Validation
{
    public static Result Run(string root)
    {
        var errors = new List<string>(); int parsed = 0;
        foreach (var file in SafeFiles.Enumerate(root))
        {
            if (new FileInfo(file).Length == 0) errors.Add($"Empty file: {Path.GetRelativePath(root, file)}");
            if (Path.GetExtension(file) == ".json")
                try { JsonNode.Parse(File.ReadAllText(file)); parsed++; } catch (JsonException) { errors.Add($"Invalid JSON: {file}"); }
        }
        foreach (var file in Directory.GetFiles(Path.Combine(root, "config"), "*.json").Concat(Directory.GetFiles(Path.Combine(root, "upstream"), "*.json")))
        {
            var instance = JsonNode.Parse(File.ReadAllText(file))!;
            var schemaReference = instance["$schema"]?.GetValue<string>();
            if (schemaReference is null) { errors.Add($"Missing schema: {Path.GetRelativePath(root, file)}"); continue; }
            if (!Uri.TryCreate(schemaReference, UriKind.Absolute, out _))
            {
                var schemaPath = Path.GetFullPath(schemaReference, Path.GetDirectoryName(file)!);
                if (!File.Exists(schemaPath)) errors.Add($"Missing schema file: {Path.GetRelativePath(root, file)}");
                else ValidateSchema(instance, JsonNode.Parse(File.ReadAllText(schemaPath))!, Path.GetRelativePath(root, file), errors);
            }
        }
        foreach (var skill in Directory.GetDirectories(Path.Combine(root, "plugins/sdeveng/skills")))
        {
            var path = Path.Combine(skill, "SKILL.md");
            if (!File.Exists(path)) { errors.Add($"Missing skill: {skill}"); continue; }
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---\n", StringComparison.Ordinal) || !text.Contains("\nname: " + Path.GetFileName(skill) + "\n", StringComparison.Ordinal) || !text.Contains("\ndescription: ", StringComparison.Ordinal)) errors.Add($"Invalid skill frontmatter: {skill}");
            if (!File.Exists(Path.Combine(root, "evals", Path.GetFileName(skill), "eval.yaml"))) errors.Add($"Missing evaluation: {skill}");
            var ui = Path.Combine(skill, "agents", "openai.yaml");
            if (!File.Exists(ui)) errors.Add($"Missing skill UI metadata: {skill}");
            else
            {
                var uiText = File.ReadAllText(ui); var name = Path.GetFileName(skill);
                if (!Regex.IsMatch(uiText, @"(?m)^interface:\s*$") || !Regex.IsMatch(uiText, @"(?m)^\s+display_name:\s+\S") || !Regex.IsMatch(uiText, @"(?m)^\s+short_description:\s+\S") || !uiText.Contains("$" + name, StringComparison.Ordinal)) errors.Add($"Invalid skill UI metadata: {skill}");
            }
        }
        errors.AddRange(RuntimeReferences.Missing(root));
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var native in Directory.GetFiles(Path.Combine(root, "agents"), "*.toml"))
        {
            var nativeText = File.ReadAllText(native);
            var name = Regex.Match(nativeText, "(?m)^name\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var model = Regex.Match(nativeText, "(?m)^model\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var effort = Regex.Match(nativeText, "(?m)^model_reasoning_effort\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            if (name.Length == 0 || !nativeNames.Add(name) || model is not ("gpt-5.6-luna" or "gpt-5.6-terra" or "gpt-5.6-sol" or "gpt-6-astra" or "gpt-5.5") || effort is not ("low" or "medium" or "high" or "xhigh" or "max" or "ultra")) errors.Add($"Invalid native agent metadata: {native}");
        }
        PluginManifests.Validate(root, errors);
        var version = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/toolkit.json")))?["version"]?.GetValue<string>();
        var portableVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/plugin.json")))?["version"]?.GetValue<string>();
        var compatibilityVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/.codex-plugin/plugin.json")))?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || portableVersion != version || compatibilityVersion != version) errors.Add("Toolkit and plugin manifest versions must match.");
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".agents/plugins/marketplace.json")))!;
        if (marketplace["plugins"]?[0]?["source"]?["path"]?.GetValue<string>() != "./plugins/sdeveng"
            || marketplace["plugins"]?[0]?["name"]?.GetValue<string>() != PluginManifests.PluginName) errors.Add("Marketplace source path or plugin identity mismatch.");
        var ecosystem = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/ecosystem.json")))!;
        if (ecosystem["pluginPath"]?.GetValue<string>() != "plugins/sdeveng"
            || ecosystem["templatePath"]?.GetValue<string>() != "templates/project"
            || ecosystem["siblingRepositoriesAreRuntimeDependencies"]?.GetValue<bool>() != false
            || Directory.GetDirectories(Path.Combine(root, "plugins")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1
            || Directory.GetDirectories(Path.Combine(root, "templates")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1)
            errors.Add("Ecosystem plugin/template topology mismatch.");
        try { Settings.Load(root, _ => null); } catch (ArgumentException e) { errors.Add(e.Message); }
        return new(errors.Count == 0 ? "ok" : "failed", new { jsonFiles = parsed, errors, note = "Structural validation includes configuration schemas, runtime references and release identity." }, errors.Count == 0 ? 0 : 1);
    }
    static void ValidateSchema(JsonNode instance, JsonNode schema, string file, List<string> errors)
    {
        if (schema["type"]?.GetValue<string>() == "object")
        {
            if (instance is not JsonObject value) { errors.Add($"Schema type mismatch: {file} must be object."); return; }
            var properties = schema["properties"]?.AsObject() ?? [];
            foreach (var required in schema["required"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []) if (!value.ContainsKey(required)) errors.Add($"Schema required key missing: {file}:{required}");
            var additional = schema["additionalProperties"];
            if (additional is JsonValue additionalValue && additionalValue.TryGetValue<bool>(out var allowed) && !allowed)
                foreach (var key in value.Select(x => x.Key)) if (!properties.ContainsKey(key)) errors.Add($"Schema unknown key: {file}:{key}");
            foreach (var property in properties) if (value[property.Key] is { } child) ValidateSchema(child, property.Value!, file + ":" + property.Key, errors);
            if (additional is JsonObject additionalSchema)
                foreach (var property in value.Where(x => !properties.ContainsKey(x.Key) && x.Value is not null)) ValidateSchema(property.Value!, additionalSchema, file + ":" + property.Key, errors);
        }
        else if (schema["type"]?.GetValue<string>() == "array")
        {
            if (instance is not JsonArray values) { errors.Add($"Schema type mismatch: {file} must be array."); return; }
            foreach (var value in values.Where(x => x is not null)) ValidateSchema(value!, schema["items"]!, file + "[]", errors);
        }
        else if (schema["type"]?.GetValue<string>() is { } type && !MatchesType(instance, type)) errors.Add($"Schema type mismatch: {file} must be {type}.");
    }
    static bool MatchesType(JsonNode node, string type) => type switch { "string" => node is JsonValue v && v.TryGetValue<string>(out _), "boolean" => node is JsonValue v && v.TryGetValue<bool>(out _), "integer" => node is JsonValue v && v.TryGetValue<int>(out _), "number" => node is JsonValue v && v.TryGetValue<double>(out _), _ => true };
}

public static class Evaluation
{
    public static Result Run(string toolkit, string? skill, string? resultsPath)
    {
        // Evaluation documents use JSON syntax, a strict YAML 1.2 subset, to stay dependency-free.
        var cases = Directory.GetFiles(Path.Combine(toolkit, "evals"), "eval.yaml", SearchOption.AllDirectories).Where(p => skill is null || Path.GetFileName(Path.GetDirectoryName(p)) == skill).ToArray();
        if (cases.Length == 0) throw new ArgumentException("No matching evaluation.");
        var runs = resultsPath is null ? null : JsonNode.Parse(File.ReadAllText(resultsPath))?.AsArray();
        var outcomes = new List<object>(); bool failed = false;
        foreach (var path in cases)
        {
            var specification = JsonNode.Parse(File.ReadAllText(path))!;
            var name = specification["skill"]!.GetValue<string>();
            if (runs is null)
            {
                var fixturePath = Path.GetFullPath(specification["fixture"]!.GetValue<string>(), Path.GetDirectoryName(path)!);
                var fixture = JsonNode.Parse(File.ReadAllText(fixturePath));
                var ok = fixture is JsonObject && specification["scenario"]?.GetValue<string>().Length > 10 && specification["expected"]?.GetValue<string>().Length > 10 && specification["safety"]?.GetValue<string>().Length > 10;
                failed |= !ok;
                outcomes.Add(new { skill = name, passed = ok, kind = "scenario/fixture integrity", agentBehaviorMeasured = false });
                continue;
            }
            var run = runs.SingleOrDefault(r => r?["skill"]?.GetValue<string>() == name);
            var failures = new List<string>();
            if (run is null) failures.Add("Missing run.");
            else
            {
                if (run["success"]?.GetValue<bool>() != true) failures.Add("Correctness failed.");
                if (run["expectedSatisfied"]?.GetValue<bool>() != true || run["safetySatisfied"]?.GetValue<bool>() != true) failures.Add("Trusted grader must attest expected and safety behavior.");
                if (string.IsNullOrWhiteSpace(run["revision"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["model"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["promptHash"]?.GetValue<string>())) failures.Add("Measured run requires revision, model and promptHash provenance.");
                foreach (var budget in specification["budgets"]!.AsObject())
                    if (run[budget.Key] is null || run[budget.Key]!.GetValue<double>() < 0 || run[budget.Key]!.GetValue<double>() > budget.Value!.GetValue<double>()) failures.Add($"Missing, invalid or exceeded {budget.Key}.");
                if (run["baseline"] is not JsonObject baseline || baseline["success"]?.GetValue<bool>() != true) failures.Add("Successful baseline required for comparison.");
                else if (baseline["tokens"] is null || baseline["tokens"]!.GetValue<double>() <= 0) failures.Add("Valid baseline token measurement required.");
                else if (run["tokens"] is not null && run["tokens"]!.GetValue<double>() > baseline["tokens"]!.GetValue<double>() * 1.10) failures.Add("Token use regressed more than 10% against baseline.");
            }
            failed |= failures.Count > 0;
            outcomes.Add(new { skill = name, passed = failures.Count == 0, failures });
        }
        return new(failed ? "failed" : "ok", new { outcomes, note = runs is null ? "Offline fixture integrity only; it does not claim agent behavior. Supply trusted, attested --results for measured regression gates." : "Attested measured runs checked against correctness, safety, provenance and regression budgets." }, failed ? 1 : 0);
    }
}
