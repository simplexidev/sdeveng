namespace SdevEng;

public record Result(string Status, object? Data, int ExitCode = 0)
{
    public static Result Ok(object? data) => new("ok", data);
    public static Result Review(string reason) => new("REVIEW", new { reason, fallback = "Codex" });
}

public record ProcessReport(int ProcessExitCode, object Summary, string Artifact)
{
    public VerificationResult? Verification { get; init; }
}
public record ProcessResult(int ExitCode, string Output);

public sealed record WorkspaceDiagnostic(string Id, string Kind, string Message, string? ProjectPath);
public sealed record SolutionWorkspaceModel(string Path, string[] Projects, string[] CompilationAvailableProjects, WorkspaceDiagnostic[] Diagnostics);
public sealed record SemanticSolutionModel(string Path, SemanticProjectModel[] Projects);
public sealed record SemanticProjectModel(string Path, SemanticNamespaceModel[] Namespaces, SemanticTypeModel[] Types)
{
    public SemanticReferenceModel[] References { get; init; } = [];
    public SemanticCallSiteModel[] CallSites { get; init; } = [];
    public SemanticTypeRelationshipModel[] TypeRelationships { get; init; } = [];
}
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
