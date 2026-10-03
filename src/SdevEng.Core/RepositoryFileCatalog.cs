namespace SdevEng;

/// <summary>A deterministic path and basename entry for repository discovery.</summary>
public sealed record RepositoryFileEntry(string Path, string Name, string[] Terms);

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
}
