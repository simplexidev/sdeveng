namespace SdevEng;

/// <summary>A deterministic path and basename entry for repository discovery.</summary>
public sealed record RepositoryFileEntry(string Path, string Name, string[] Terms);

/// <summary>A compact ranked repository search candidate.</summary>
public sealed record RepositorySearchCandidate(string Path, int Score, string Match);

/// <summary>A sorted, lightweight index of repository file paths and names.</summary>
public sealed record RepositoryFileCatalog(int IndexVersion, RepositoryFileEntry[] Files)
{
    public const int CurrentIndexVersion = 1;

    /// <summary>Checks that entries retain the canonical ordering and bounded term contract.</summary>
    public bool HasValidIntegrity() => IndexVersion == CurrentIndexVersion
        && Files is not null
        && Files.All(file => file is not null
            && !string.IsNullOrEmpty(file.Path)
            && file.Path == file.Path.Replace('\\', '/')
            && !file.Path.StartsWith("/", StringComparison.Ordinal)
            && file.Name == System.IO.Path.GetFileName(file.Path)
            && file.Terms is not null
            && file.Terms.Length <= 256
            && file.Terms.SequenceEqual(file.Terms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        && Files.Select(file => file.Path).SequenceEqual(Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    public static RepositoryFileCatalog Create(IEnumerable<string> paths, IReadOnlyDictionary<string, string[]>? terms = null) => new(CurrentIndexVersion, paths
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(path => new RepositoryFileEntry(path.Replace('\\', '/'), System.IO.Path.GetFileName(path), terms?.GetValueOrDefault(path) ?? []))
        .ToArray());

    public RepositoryFileEntry[] Find(string query) => Files
        .Where(file => file.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
            || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || file.Terms.Contains(query, StringComparer.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>Finds one canonical repository-relative path exactly.</summary>
    public RepositoryFileEntry[] FindExactPath(string path)
    {
        var canonicalPath = path.Replace('\\', '/').TrimStart('/');
        return Files.Where(file => string.Equals(file.Path, canonicalPath, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Finds basenames containing the requested text, without matching directory paths or file contents.</summary>
    public RepositoryFileEntry[] FindName(string query) => Files
        .Where(file => file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>Ranks candidates for exact text in indexed paths, names, or bounded text terms.</summary>
    public RepositorySearchCandidate[] SearchExactText(string query, int limit) => Search(query, limit, fuzzy: false);

    /// <summary>Ranks candidates whose indexed terms are within one edit of a bounded fuzzy query.</summary>
    public RepositorySearchCandidate[] SearchFuzzyTerms(string query, int limit) => Search(query, limit, fuzzy: true);

    private RepositorySearchCandidate[] Search(string query, int limit, bool fuzzy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        var terms = System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}_-]{2,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        if (terms.Length == 0) throw new ArgumentException("Search query must contain at least one term of two or more characters.", nameof(query));
        return Files.Select(file =>
            {
                var exactPath = file.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
                var exactName = file.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var exactTerms = terms.Count(term => file.Terms.Contains(term, StringComparer.OrdinalIgnoreCase));
                var fuzzyTerms = fuzzy ? terms.Count(term => file.Terms.Any(candidate => EditDistanceAtMostOne(term, candidate))) : 0;
                var score = (exactPath ? 100 : 0) + (exactName ? 80 : 0) + exactTerms * 20 + fuzzyTerms * 10;
                var match = exactPath || exactName || exactTerms > 0 ? "exact" : fuzzyTerms > 0 ? "fuzzy-term" : "";
                return new RepositorySearchCandidate(file.Path, score, match);
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Take(limit).ToArray();
    }

    private static bool EditDistanceAtMostOne(string left, string right)
    {
        if (left == right) return true;
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        var i = 0; var j = 0; var edits = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] == right[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (left.Length > right.Length) i++;
            else if (right.Length > left.Length) j++;
            else { i++; j++; }
        }
        return edits + (i < left.Length || j < right.Length ? 1 : 0) <= 1;
    }
}
