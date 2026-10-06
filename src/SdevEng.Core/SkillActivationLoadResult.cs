namespace SdevEng;

public sealed record SkillReferenceRequest(string SkillId, string ReferencePath);
public sealed record SkillActivationLoadEvidence(SkillLoadResult Load, long Tokens);

/// <summary>Actual admission evidence for one role/WorkUnit activation and load operation.</summary>
public sealed record SkillActivationLoadResult(
    int SchemaVersion, SkillActivationContext Context, int SkillsTokenBudget,
    TokenizerManifest Tokenizer, IReadOnlyList<string> ActivatedSkillIds,
    long ConsumedTokens, long RemainingTokens, IReadOnlyList<SkillActivationLoadEvidence> Evidence)
{
    public void Validate()
    {
        Context.Validate();
        Tokenizer.Validate();
        if (SchemaVersion != 1 || SkillsTokenBudget < 0 || ConsumedTokens < 0 ||
            ConsumedTokens > SkillsTokenBudget || RemainingTokens != SkillsTokenBudget - ConsumedTokens ||
            ActivatedSkillIds.Any(string.IsNullOrWhiteSpace) ||
            ActivatedSkillIds.Distinct(StringComparer.Ordinal).Count() != ActivatedSkillIds.Count ||
            Evidence.Count < ActivatedSkillIds.Count || Evidence.Count > ActivatedSkillIds.Count + 1024)
            throw new ArgumentException("Invalid skill activation/load evidence.");
        long consumed = 0;
        var acceptedBodies = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<SkillReferenceRequest>();
        for (var index = 0; index < Evidence.Count; index++)
        {
            var item = Evidence[index];
            var load = item.Load;
            if (item.Tokens < 0 || string.IsNullOrWhiteSpace(load.SkillId) || load.Status is not ("loaded" or "omitted") ||
                (index < ActivatedSkillIds.Count ? load.ReferencePath is not null || load.SkillId != ActivatedSkillIds[index] :
                    string.IsNullOrWhiteSpace(load.ReferencePath) || !references.Add(new(load.SkillId, load.ReferencePath))))
                throw new ArgumentException("Invalid skill load measurement.");
            if (load.Status == "loaded")
            {
                if (!ActivatedSkillIds.Contains(load.SkillId, StringComparer.Ordinal) || load.Content is null ||
                    load.OmissionReason is not null || load.Characters != load.Content.Length ||
                    load.Utf8Bytes != System.Text.Encoding.UTF8.GetByteCount(load.Content) ||
                    item.Tokens > SkillsTokenBudget - consumed ||
                    (load.ReferencePath is not null && !acceptedBodies.Contains(load.SkillId)))
                    throw new ArgumentException("Invalid accepted skill evidence.");
                consumed = checked(consumed + item.Tokens);
                if (load.ReferencePath is null) acceptedBodies.Add(load.SkillId);
            }
            else if (load.Content is not null || load.Utf8Bytes != 0 || load.Characters != 0 ||
                     load.OmissionReason is not ("skill-not-activated" or "unknown-reference" or "unsafe-path" or
                         "hash-mismatch" or "content-unavailable" or "invalid-instructions" or "invalid-utf8" or
                         "skills-budget-exceeded" or "instructions-not-loaded") ||
                     (load.OmissionReason == "skills-budget-exceeded" ? item.Tokens <= SkillsTokenBudget - consumed : item.Tokens != 0) ||
                     (load.OmissionReason == "instructions-not-loaded" &&
                         (load.ReferencePath is null || !ActivatedSkillIds.Contains(load.SkillId, StringComparer.Ordinal) || acceptedBodies.Contains(load.SkillId))))
                throw new ArgumentException("Invalid omitted skill evidence.");
        }
        if (consumed != ConsumedTokens) throw new ArgumentException("Skill token totals do not match accepted evidence.");
    }
}
