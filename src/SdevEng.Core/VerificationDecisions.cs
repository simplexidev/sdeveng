namespace SdevEng;

public static class VerificationDecisions
{
    public static VerificationDecision Evaluate(string check, IEnumerable<VerificationResult> evidence, VerificationEvidencePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(policy);
        var results = evidence.ToArray();
        if (results.Any(r => r is null || r.SchemaVersion is not (1 or 2) || r.Check != check ||
            r.Source is not ("local" or "hosted") || r.Status is not ("passed" or "failed" or "cancelled" or "timed-out") ||
            r.SchemaVersion == 1 && r.Status is ("cancelled" or "timed-out") ||
            (r.ExitCode == 0) != (r.Status == "passed") || r.ExitCode < 0 || string.IsNullOrWhiteSpace(r.Artifact)) ||
            results.GroupBy(r => r.Source, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Evidence must contain at most one valid result per source for the requested check.", nameof(evidence));
        var local = results.SingleOrDefault(r => r.Source == "local");
        var hosted = results.SingleOrDefault(r => r.Source == "hosted");
        var reasons = new List<string>();
        if (policy.RequireLocal && local is null) reasons.Add("local-missing");
        if (policy.RequireHosted && hosted is null) reasons.Add("hosted-missing");
        if (local?.Status is { } localStatus && localStatus != "passed") reasons.Add("local-" + localStatus);
        if (hosted?.Status is { } hostedStatus && hostedStatus != "passed") reasons.Add("hosted-" + hostedStatus);
        var disagrees = local is not null && hosted is not null && local.Status != hosted.Status;
        if (disagrees) reasons.Add("local-hosted-disagreement");
        if (results.Length == 0) reasons.Add("evidence-missing");
        return new(check, reasons.Count == 0, disagrees, reasons);
    }
}
