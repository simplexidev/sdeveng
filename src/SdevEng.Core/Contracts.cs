namespace SdevEng;

public record Result(string Status, object? Data, int ExitCode = 0)
{
    public static Result Ok(object? data) => new("ok", data);
    public static Result Review(string reason) => new("REVIEW", new { reason, fallback = "Codex" });
}

/// <summary>A discoverable worker tool that delegates to a canonical CLI fact command.</summary>
public sealed record ToolDescriptor(string Name, string Description, string Command, string Kind, int SchemaVersion, bool ReadOnly, System.Text.Json.JsonElement InputSchema);

public record ProcessReport(int ProcessExitCode, object Summary, string Artifact)
{
    public VerificationResult? Verification { get; init; }
}
public record ProcessResult(int ExitCode, string Output);

public sealed record WorkspaceDiagnostic(string Id, string Kind, string Message, string? ProjectPath);
public sealed record SolutionWorkspaceModel(string Path, string[] Projects, string[] CompilationAvailableProjects, WorkspaceDiagnostic[] Diagnostics);
public sealed record SemanticSolutionModel(string Path, SemanticProjectModel[] Projects)
{
    public SemanticProjectEdgeModel[] ProjectEdges { get; init; } = [];
    public SemanticProjectDependencyModel[] ProjectDependencies { get; init; } = [];

    /// <summary>Finds declarations and callers transitively affected by a declared symbol.</summary>
    public SemanticImpactResult AffectedSymbols(string symbol, int limit)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("A symbol key is required.", nameof(symbol));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var declarations = Projects.SelectMany(project => project.Types.SelectMany(type =>
            new[] { type.StableKey }.Concat(type.Callables.Select(item => item.StableKey)).Concat(type.DataMembers.Select(item => item.StableKey))));
        if (!declarations.Contains(symbol, StringComparer.Ordinal)) throw new ArgumentException("Symbol is not in the solution.", nameof(symbol));
        var affected = new HashSet<string>(StringComparer.Ordinal) { symbol };
        var references = Projects.SelectMany(project => project.References.Select(item => (item.TargetKey, item.CallerKey))
            .Concat(project.CallSites.Select(item => (item.TargetKey, item.CallerKey)))).ToArray();
        bool added;
        do
        {
            added = false;
            foreach (var reference in references)
                if (reference.CallerKey is not null && affected.Contains(reference.TargetKey)) added |= affected.Add(reference.CallerKey);
        } while (added);
        var matches = affected.Order(StringComparer.Ordinal).ToArray();
        return new(matches.Take(limit).ToArray(), matches.Length, limit, matches.Length > limit);
    }

    /// <summary>Finds files declaring or referencing symbols declared in a changed source file.</summary>
    public SemanticImpactResult AffectedFiles(string file, int limit)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("A repository-relative file is required.", nameof(file));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var path = file.Replace('\\', '/');
        var declarations = Projects.SelectMany(project => project.Types.SelectMany(type =>
            new[] { (type.StableKey, type.Location) }
                .Concat(type.Callables.Select(item => (item.StableKey, item.Location)))
                .Concat(type.DataMembers.Select(item => (item.StableKey, item.Location))))).ToArray();
        var source = declarations.Select(item => SourcePath(item.Location))
            .FirstOrDefault(item => item == path || item.EndsWith("/" + path, StringComparison.Ordinal));
        var roots = declarations.Where(item => SourcePath(item.Location) == source).Select(item => item.StableKey).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0) throw new ArgumentException("File has no declarations in the solution.", nameof(file));
        var prefix = source![..^path.Length];
        var affected = roots.SelectMany(symbol => AffectedSymbols(symbol, int.MaxValue).Items).ToHashSet(StringComparer.Ordinal);
        var files = declarations.Where(item => affected.Contains(item.StableKey)).Select(item => SourcePath(item.Location))
            .Concat(Projects.SelectMany(project => project.References.Select(item => (item.TargetKey, item.Location))
                .Concat(project.CallSites.Select(item => (item.TargetKey, item.Location))))
                .Where(item => affected.Contains(item.TargetKey)).Select(item => SourcePath(item.Location)))
            .Select(item => item.StartsWith(prefix, StringComparison.Ordinal) ? item[prefix.Length..] : item)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(files.Take(limit).ToArray(), files.Length, limit, files.Length > limit);
    }

    private static string SourcePath(string location)
    {
        var column = location.LastIndexOf(':');
        var line = column < 0 ? -1 : location.LastIndexOf(':', column - 1);
        return line < 0 ? location : location[..line];
    }

    public SemanticTestCandidateResult TestCandidates(string symbol, int limit)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("A symbol key is required.", nameof(symbol));
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var owners = Projects.Where(project => project.Types.Any(type => type.StableKey == symbol ||
            type.Callables.Any(callable => callable.StableKey == symbol) ||
            type.DataMembers.Any(member => member.StableKey == symbol))).Select(project => project.Path).ToHashSet(StringComparer.Ordinal);
        if (owners.Count == 0) throw new ArgumentException("Symbol is not in the solution.", nameof(symbol));
        var direct = Projects.Where(project => project.IsTest).SelectMany(project => project.TestMethods
            .Where(method => project.References.Any(reference => reference.TargetKey == symbol && reference.CallerKey == method.StableKey) ||
                project.CallSites.Any(call => call.TargetKey == symbol && call.CallerKey == method.StableKey))
            .Select(method => new SemanticTestCandidateModel(project.Path, method.StableKey, method.Location, "direct-reference")));
        var dependentProjects = ProjectDependencies.Where(edge => owners.Contains(edge.TargetProject)).Select(edge => edge.SourceProject).ToHashSet(StringComparer.Ordinal);
        var dependencies = Projects.Where(project => project.IsTest && dependentProjects.Contains(project.Path))
            .Select(project => new SemanticTestCandidateModel(project.Path, null, null, "project-dependency"));
        var matches = direct.Concat(dependencies).Distinct().OrderBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.Reason, StringComparer.Ordinal).ThenBy(item => item.TestKey, StringComparer.Ordinal).ToArray();
        return new(matches.Take(limit).ToArray(), matches.Length, limit, matches.Length > limit);
    }

    public SemanticRelationshipQueryResult Relationships(string project, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Projects.Any(item => item.Path == project)) throw new ArgumentException("Project is not in the solution.", nameof(project));
        var matches = ProjectEdges.Where(edge => edge.SourceProject == project || edge.TargetProject == project).ToArray();
        return new(matches.Take(limit).ToArray(), matches.Length, limit, matches.Length > limit);
    }
}
public sealed record SemanticImpactResult(string[] Items, int Total, int Limit, bool Truncated);
public sealed record SemanticProjectDependencyModel(string SourceProject, string TargetProject);
public sealed record SemanticTestCandidateModel(string Project, string? TestKey, string? Location, string Reason);
public sealed record SemanticTestCandidateResult(SemanticTestCandidateModel[] Candidates, int Total, int Limit, bool Truncated);
public sealed record SemanticProjectEdgeModel(string SourceProject, string TargetProject, string SourceKey, string TargetKey, string Kind, string Location);
public sealed record SemanticRelationshipQueryResult(SemanticProjectEdgeModel[] Edges, int Total, int Limit, bool Truncated);
public sealed record SemanticProjectModel(string Path, SemanticNamespaceModel[] Namespaces, SemanticTypeModel[] Types)
{
    public bool IsTest { get; init; }
    public string TestFramework { get; init; } = "unknown";
    public SemanticTestMethodModel[] TestMethods { get; init; } = [];
    public SemanticReferenceModel[] References { get; init; } = [];
    public SemanticCallSiteModel[] CallSites { get; init; } = [];
    public SemanticTypeRelationshipModel[] TypeRelationships { get; init; } = [];
}
public sealed record SemanticTestMethodModel(string StableKey, string Location, string Framework);
public sealed record SemanticTypeRelationshipModel(string SourceKey, string TargetKey, string Kind);
public sealed record SemanticReferenceModel(string TargetKey, string Location, string? CallerKey);
public sealed record SemanticCallSiteModel(string TargetKey, string Location, string? CallerKey);
public sealed record SemanticNamespaceModel(string Name);
public sealed record SemanticTypeModel(string Name, string Kind, string Accessibility, string[] BaseTypes, string[] Members, SemanticCallableModel[] Callables)
{
    public string StableKey { get; init; } = "";
    public string Location { get; init; } = "";
    public SemanticMemberModel[] DataMembers { get; init; } = [];
}
public sealed record SemanticCallableModel(string Name, string Kind, string Accessibility, string Location)
{
    public string StableKey { get; init; } = "";
}
public sealed record SemanticMemberModel(string Name, string Kind, string Accessibility, string Location)
{
    public string StableKey { get; init; } = "";
}

public sealed record VerificationResult(int SchemaVersion, string Source, string Check, string Status, int ExitCode, string Artifact)
{
    public string? EnvironmentIdentity { get; init; }
}

public sealed record VerificationEvidencePolicy(bool RequireLocal, bool RequireHosted);
public sealed record VerificationDecision(string Check, bool CanProgress, bool Disagrees, IReadOnlyList<string> Reasons);
