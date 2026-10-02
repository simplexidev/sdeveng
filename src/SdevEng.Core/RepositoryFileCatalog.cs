namespace SdevEng;

/// <summary>A deterministic path and basename entry for repository discovery.</summary>
public sealed record RepositoryFileEntry(string Path, string Name);

/// <summary>A sorted, lightweight index of repository file paths and names.</summary>
public sealed record RepositoryFileCatalog(RepositoryFileEntry[] Files)
{
    public static RepositoryFileCatalog Create(IEnumerable<string> paths) => new(paths
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Select(path => new RepositoryFileEntry(path.Replace('\\', '/'), System.IO.Path.GetFileName(path)))
        .ToArray());

    public RepositoryFileEntry[] Find(string query) => Files
        .Where(file => file.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
            || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        .ToArray();
}
