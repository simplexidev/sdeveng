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

public sealed record VerificationResult(int SchemaVersion, string Source, string Check, string Status, int ExitCode, string Artifact)
{
    public string? EnvironmentIdentity { get; init; }
}

public sealed record VerificationEvidencePolicy(bool RequireLocal, bool RequireHosted);
public sealed record VerificationDecision(string Check, bool CanProgress, bool Disagrees, IReadOnlyList<string> Reasons);
