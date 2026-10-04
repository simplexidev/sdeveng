using System.Security.Cryptography;
using System.Text;

namespace SdevEng;

/// <summary>Resolves a single indexed item through a repository root with strict byte and line bounds.</summary>
public static class RepositoryEvidenceExpander
{
    public const int MaximumUtf8Bytes = 16 * 1024;
    public const int MaximumLines = 200;

    public static EvidenceExpansionResult Expand(string root, EvidenceExpansionRequest request, string currentPackId,
        string currentPackRevision, IEnumerable<EvidenceExpansionItem> indexedItems, int remainingRoleAllowanceLines)
    {
        ArgumentNullException.ThrowIfNull(indexedItems);
        var items = indexedItems.ToArray();
        var validation = EvidenceExpansionRequestValidator.Validate(request, currentPackId, currentPackRevision,
            items.Select(item => item.Id), remainingRoleAllowanceLines);
        if (!validation.IsValid) return new(EvidenceExpansionStatus.Rejected, request.EvidenceId, null, null, null, 0, false, validation.Rejection);
        var item = items.SingleOrDefault(candidate => candidate.Id == request.EvidenceId);
        if (item is null || string.IsNullOrWhiteSpace(item.SourceRevision)) return Omitted(request.EvidenceId);
        if (request.StartLine is not int start || request.EndLine is not int end) return Omitted(request.EvidenceId);

        var lineCount = (long)end - start + 1;
        if (lineCount > MaximumLines || lineCount > remainingRoleAllowanceLines)
            return new(EvidenceExpansionStatus.BudgetExceeded, request.EvidenceId, null, start, end, 0, false);

        if (!item.LocationKey.StartsWith("repo:", StringComparison.Ordinal)) return Omitted(request.EvidenceId);
        var relative = item.LocationKey[5..].Split('#')[0];
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or "..")) return Omitted(request.EvidenceId);
        var repositoryRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return Omitted(request.EvidenceId);
        try { SafeFiles.NoLinks(path); }
        catch (IOException) { return Omitted(request.EvidenceId); }
        if (!File.Exists(path)) return Omitted(request.EvidenceId);

        // Hash using a streaming reader so stale-source validation does not materialize the file contents.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(hash, item.SourceRevision, StringComparison.Ordinal)) return Omitted(request.EvidenceId);
        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var selected = new List<string>();
        var lineNumber = 0;
        var selectedBytes = 0;
        while (reader.Peek() >= 0 && lineNumber < end)
        {
            lineNumber++;
            var buffer = new StringBuilder();
            int character;
            while ((character = reader.Read()) >= 0 && character is not ('\r' or '\n'))
            {
                if (lineNumber < start) continue;
                // Even one enormous line must not be materialized before the byte guard.
                if (buffer.Length >= MaximumUtf8Bytes)
                    return new(EvidenceExpansionStatus.BudgetExceeded, request.EvidenceId, null, start, end, 0, false);
                buffer.Append((char)character);
            }
            if (character == '\r' && reader.Peek() == '\n') reader.Read();
            if (lineNumber < start) continue;
            var line = buffer.ToString();
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (selected.Count == 0 ? 0 : 1);
            if ((long)selectedBytes + lineBytes > MaximumUtf8Bytes)
                return new(EvidenceExpansionStatus.BudgetExceeded, request.EvidenceId, null, start, end, 0, false);
            selectedBytes += lineBytes;
            selected.Add(line);
        }
        if (selected.Count != lineCount) return Omitted(request.EvidenceId);
        var excerpt = string.Join('\n', selected);
        return new(EvidenceExpansionStatus.Expanded, request.EvidenceId, excerpt, start, end, selectedBytes, false);
    }

    private static EvidenceExpansionResult Omitted(string id) => new(EvidenceExpansionStatus.Omitted, id, null, null, null, 0, false);
}
