using SdevEng.Infrastructure;

namespace SdevEng;

public sealed partial class SkillActivationService
{
    /// <summary>Activates once, admits bodies in priority order, then individually requested references.</summary>
    public async Task<SkillActivationLoadResult> ActivateAndLoadAsync(string toolkitRoot,
        SkillActivationContext context, TokenizerRegistry tokenizers, string tokenizerId,
        IReadOnlyList<SkillReferenceRequest> references, int? modelContextTokens = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        ArgumentNullException.ThrowIfNull(tokenizers);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerId);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > 1024 || references.Any(request => request is null ||
                string.IsNullOrWhiteSpace(request.SkillId) || string.IsNullOrWhiteSpace(request.ReferencePath)) ||
            references.Distinct().Count() != references.Count)
            throw new ArgumentException("At most 1024 unique individual references are supported.", nameof(references));
        var budget = new ContextBudgetService(Path.Combine(toolkitRoot, "config", "context-budget-policy.json"))
            .ForRole(context.Role, modelContextTokens);
        var tokenizer = tokenizers.Resolve(tokenizerId);
        var activated = await ActivateAsync(toolkitRoot, context, cancellationToken).ConfigureAwait(false);
        var byId = activated.ToDictionary(skill => skill.Id!, StringComparer.Ordinal);
        var acceptedBodies = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new List<SkillActivationLoadEvidence>();
        long consumed = 0;

        void Admit(SkillLoadResult load)
        {
            var tokens = load.Status == "loaded" ? tokenizer.CountTokens(load.Content!) : 0;
            if (tokens < 0) throw new InvalidDataException("Tokenizer returned a negative count.");
            if (tokens > budget.SkillsTokens - consumed)
                load = new(load.SkillId, load.ReferencePath, "omitted", null, "skills-budget-exceeded", 0, 0);
            if (load.Status == "loaded")
            {
                consumed += tokens;
                if (load.ReferencePath is null) acceptedBodies.Add(load.SkillId);
            }
            evidence.Add(new(load, tokens));
        }

        foreach (var skill in activated)
            Admit(await LoadActivatedAsync(toolkitRoot, skill.Id!, skill, null, cancellationToken).ConfigureAwait(false));
        foreach (var request in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byId.TryGetValue(request.SkillId, out var skill);
            // Validate unknown references through the same loader; never load references for an omitted body.
            if (skill is not null && !acceptedBodies.Contains(request.SkillId) &&
                (skill.Resources ?? []).Any(resource => resource.Type == "reference" && resource.Path == request.ReferencePath))
                Admit(new(request.SkillId, request.ReferencePath, "omitted", null, "instructions-not-loaded", 0, 0));
            else
                Admit(await LoadActivatedAsync(toolkitRoot, request.SkillId, skill, request.ReferencePath, cancellationToken).ConfigureAwait(false));
        }
        var result = new SkillActivationLoadResult(1, context, budget.SkillsTokens, tokenizer.Manifest,
            activated.Select(skill => skill.Id!).ToArray(), consumed, budget.SkillsTokens - consumed, evidence);
        result.Validate();
        return result;
    }
}
