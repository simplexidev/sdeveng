namespace SdevEng;

public sealed class VerificationCommandModule : AgentTool.ICommandModule
{
    public bool CanHandle(Cli command) => command.Command == "verification decide";

    public Task<Result> Execute(Cli command, string toolkit, string root, Settings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        command.ValidateCommand(command.Command);
        var commit = command.Require("commit");
        var policy = VerificationEvidenceFiles.ReadPolicy(Path.Combine(toolkit, "config", "verification-evidence-policy.json"));
        var hosted = VerificationEvidenceFiles.ReadHosted(command.Require("hosted-file"), commit);
        var local = command.Get("local-file") is { } path ? VerificationEvidenceFiles.ReadLocal(path, commit) : [];
        var decisions = policy.RequiredChecks.Select(check => VerificationDecisions.Evaluate(check,
            local.Where(result => result.Check == check).Concat(hosted.Where(result => result.Check == check)), policy.Sources)).ToArray();
        var canProgress = decisions.All(decision => decision.CanProgress);
        return Task.FromResult(new Result(canProgress ? "ok" : "failed", new
        {
            kind = "verification-progression-decision",
            schemaVersion = 1,
            commitSha = commit,
            canProgress,
            disagreements = decisions.Where(decision => decision.Disagrees).Select(decision => decision.Check).ToArray(),
            decisions
        }, canProgress ? 0 : 1));
    }
}
