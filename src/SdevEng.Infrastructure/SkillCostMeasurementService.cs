using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Measures skill component costs against the same rendered input and tokenizer used by production.</summary>
public sealed class SkillCostMeasurementService(IRenderedInputTokenCounter tokenCounter)
{
    public SkillCostMeasurement Explain(string toolkitRoot, SkillCostExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observation = request.Observation;
        observation.Activation.Validate();
        request.Tokenizer.Validate();
        request.Template.Validate();
        if (request.Template.TokenizerId != request.Tokenizer.Id || request.Template.TokenizerRevision != request.Tokenizer.Revision)
            throw new ArgumentException("Explain template and tokenizer pins do not match.");
        var inventory = SkillCompatibilityMapReader.ReadMetadataIndex(toolkitRoot);
        var canonical = inventory.Select(s => new SkillCatalogIdentity(s.Id!, s.Version!)).ToArray();
        if (observation.Role != observation.Activation.Context.Role ||
            !canonical.OrderBy(s => s.Id, StringComparer.Ordinal).SequenceEqual(observation.Catalog.OrderBy(s => s.Id, StringComparer.Ordinal)) ||
            System.Text.Json.JsonSerializer.Serialize(request.Tokenizer, InfrastructureJson.Options) !=
            System.Text.Json.JsonSerializer.Serialize(observation.Activation.Tokenizer, InfrastructureJson.Options))
            throw new ArgumentException("Observation role, catalog revision or tokenizer does not match activation.");
        var loads = observation.Activation.Evidence.Select(e => e.Load).ToArray();
        var identities = new SkillCostIdentitySets(observation.Role, canonical, observation.ConsideredSkillIds,
            observation.Activation.ActivatedSkillIds,
            loads.Where(l => l.ReferencePath is null && l.Status == "loaded").Select(l => l.SkillId).Distinct(StringComparer.Ordinal).ToArray(),
            loads.Where(l => l.ReferencePath is not null && l.Status == "loaded").Select(l => l.SkillId).Distinct(StringComparer.Ordinal).ToArray());
        var prompt = request.Prompt;
        prompt.Validate();
        if (prompt.SchemaVersion != 2) throw new ArgumentException("Explain requires prompt manifest v2.");
        var text = new Dictionary<string, string>(request.TextByContentReference, StringComparer.Ordinal);
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var components = prompt.Components.ToList();
        foreach (var component in components.Where(c => c.Id is PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions or PromptComponentId.SkillReferences))
        {
            var skill = inventory.SingleOrDefault(s => component.ContentReference.StartsWith($"{s.Id}/{s.Version}/", StringComparison.Ordinal))
                ?? throw new ArgumentException("Skill component is outside the observed catalog revision.");
            if (component.Role != observation.Role ||
                (component.Id == PromptComponentId.SkillInstructions && component.ContentReference != $"{skill.Id}/{skill.Version}/instructions"))
                throw new ArgumentException("Skill component role or instruction identity contradicts observation.");
            if (component.Id == PromptComponentId.SkillMetadata) continue;
            var path = component.Id == PromptComponentId.SkillInstructions ? null : component.ContentReference[$"{skill.Id}/{skill.Version}/".Length..];
            if (path is not null && !(skill.Resources ?? []).Any(r => r.Type == "reference" && r.Path == path && r.Hash == component.ContentHash))
                throw new ArgumentException("Reference descriptor is outside canonical version/hash semantics.");
            var load = loads.SingleOrDefault(l => l.SkillId == skill.Id && l.ReferencePath == path);
            if (component.IsLoaded != (load?.Status == "loaded")) throw new ArgumentException("Prompt load state contradicts activation evidence.");
            if (load?.Status == "loaded")
            {
                if (!text.TryGetValue(component.ContentReference, out var content) || content != load.Content)
                    throw new ArgumentException("Prompt content contradicts load evidence.");
            }
            else reasons[component.ContentReference] = load?.OmissionReason ?? "skill-not-activated";
        }
        foreach (var load in loads.Where(l => l.Status == "loaded"))
        {
            var skill = inventory.Single(s => s.Id == load.SkillId);
            var reference = $"{skill.Id}/{skill.Version}/{load.ReferencePath ?? "instructions"}";
            if (!components.Any(c => c.ContentReference == reference && c.Id == (load.ReferencePath is null ? PromptComponentId.SkillInstructions : PromptComponentId.SkillReferences)))
                throw new ArgumentException("Loaded evidence is absent from final prompt.");
        }
        var result = MeasureLoadedReferences(toolkitRoot, request.Template.Id, request.Template.Revision, request.Template.Checksum,
            prompt, text, loads.Where(l => l.ReferencePath is not null && components.Any(c => c.ContentReference.EndsWith("/" + l.ReferencePath, StringComparison.Ordinal) && ComponentSkillId(c.ContentReference) == l.SkillId)).ToArray(), reasons,
            canonical.Select(s => s.Id).ToArray(), identities.Considered, identities.Activated) with
        { Identities = identities };
        var costs = result.Components.ToList();
        foreach (var id in identities.Considered)
        {
            if (costs.Any(c => c.SkillId == id && c.ComponentKind == "instructions")) continue;
            var skill = canonical.Single(s => s.Id == id);
            var load = loads.SingleOrDefault(l => l.SkillId == id && l.ReferencePath is null);
            costs.Add(new(id, "instructions", $"{id}/{skill.Version}/instructions", null, load?.OmissionReason ?? "skill-not-activated"));
        }
        result = result with { Components = costs };
        result.Validate();
        return result;
    }

    /// <summary>Projects individually authorized lazy-load results using canonical resource identities and hashes.</summary>
    public SkillCostMeasurement MeasureLoadedReferences(string toolkitRoot, string templateId, string templateRevision,
        string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference,
        IReadOnlyList<SkillLoadResult> referenceLoads,
        IReadOnlyDictionary<string, string>? omissionReasons = null,
        IReadOnlyCollection<string>? availableSkillIds = null,
        IReadOnlyCollection<string>? consideredSkillIds = null,
        IReadOnlyCollection<string>? activatedSkillIds = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(textByContentReference);
        ArgumentNullException.ThrowIfNull(referenceLoads);
        prompt.Validate();
        if (prompt.SchemaVersion != 2) throw new ArgumentException("Reference loads require prompt manifest v2.");
        var inventory = SkillCompatibilityMapReader.ReadMetadataIndex(toolkitRoot);
        var text = new Dictionary<string, string>(textByContentReference, StringComparer.Ordinal);
        var components = prompt.Components.ToList();
        var reasons = new Dictionary<string, string>(omissionReasons ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var load in referenceLoads)
        {
            var skill = inventory.Single(s => s.Id == load.SkillId);
            var resource = (skill.Resources ?? []).Single(r => r.Type == "reference" && r.Path == load.ReferencePath);
            var reference = $"{skill.Id}/{skill.Version}/{resource.Path}";
            if (referenceLoads.Count(l => l.SkillId == load.SkillId && l.ReferencePath == load.ReferencePath) != 1) throw new ArgumentException("Duplicate reference load.");
            var index = components.FindIndex(c => c.Id == PromptComponentId.SkillReferences && c.ContentReference == reference);
            if (index < 0 || components[index].ContentHash != resource.Hash)
                throw new ArgumentException("Reference load requires an explicit canonical hash-pinned manifest descriptor.");
            if (load.Status == "loaded")
            {
                if (load.Content is null || load.OmissionReason is not null || load.Characters != load.Content.Length ||
                    load.Utf8Bytes != System.Text.Encoding.UTF8.GetByteCount(load.Content) ||
                    resource.Hash != "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(load.Content))).ToLowerInvariant())
                    throw new ArgumentException("Invalid hash-verified reference load.");
                components[index] = components[index] with { IsLoaded = true, ContentHash = resource.Hash };
                text[reference] = load.Content;
                reasons[reference] = "loaded";
            }
            else
            {
                if (load.Status != "omitted" || load.Content is not null || load.Utf8Bytes != 0 || load.Characters != 0 || string.IsNullOrWhiteSpace(load.OmissionReason))
                    throw new ArgumentException("Invalid omitted reference load.");
                components[index] = components[index] with { IsLoaded = false };
                reasons[reference] = load.OmissionReason;
            }
        }
        return Measure(templateId, templateRevision, templateChecksum, prompt with { Components = components }, text, reasons,
            availableSkillIds, consideredSkillIds, activatedSkillIds);
    }

    public SkillCostMeasurement Measure(string templateId, string templateRevision, string templateChecksum,
        PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference,
        IReadOnlyDictionary<string, string>? omissionReasons = null,
        IReadOnlyCollection<string>? availableSkillIds = null,
        IReadOnlyCollection<string>? consideredSkillIds = null,
        IReadOnlyCollection<string>? activatedSkillIds = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(textByContentReference);
        omissionReasons ??= new Dictionary<string, string>(StringComparer.Ordinal);
        prompt.Validate();
        // Omitted bodies remain in the observation, but are not part of the actual input.
        var renderable = prompt with
        {
            Components = prompt.Components.Where(c => c.IsLoaded ||
            (c.Id is not (PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions) &&
             (prompt.SchemaVersion == 1 || c.Id != PromptComponentId.SkillReferences))).ToArray()
        };
        var measured = tokenCounter.CountAttributed(templateId, templateRevision, templateChecksum, renderable, textByContentReference);
        measured.Validate();
        var skillComponents = prompt.Components.Where(c => c.Id is PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions or PromptComponentId.SkillReferences).ToArray();
        var costs = new List<SkillComponentTokenCost>(skillComponents.Length);
        long skillTokens = 0;
        foreach (var component in skillComponents)
        {
            var kind = component.Id switch
            {
                PromptComponentId.SkillMetadata => "metadata",
                PromptComponentId.SkillInstructions => "instructions",
                _ => "reference"
            };
            if (!component.IsLoaded || !textByContentReference.ContainsKey(component.ContentReference))
            {
                costs.Add(new(ComponentSkillId(component.ContentReference), kind, component.ContentReference, null,
                    omissionReasons.GetValueOrDefault(component.ContentReference, component.Id == PromptComponentId.SkillReferences && prompt.SchemaVersion == 1 ? "reference-cost-deferred" : "content-not-loaded")));
                continue;
            }
            var tokens = measured.Attribution?.Components.Where(d => d.Span.ComponentId == PromptComponentIdJsonConverter.ToWireValue(component.Id) &&
                    d.Span.ContentReference == component.ContentReference).Sum(d => d.Tokens) ?? 0;
            skillTokens = checked(skillTokens + tokens);
            costs.Add(new(ComponentSkillId(component.ContentReference), kind, component.ContentReference, tokens, null));
        }

        var availableIds = (availableSkillIds ?? skillComponents.Where(c => c.Id == PromptComponentId.SkillMetadata).Select(c => ComponentSkillId(c.ContentReference)).ToArray()).Distinct(StringComparer.Ordinal).ToArray();
        var consideredIds = (consideredSkillIds ?? skillComponents.Where(c => c.Id == PromptComponentId.SkillMetadata).Select(c => ComponentSkillId(c.ContentReference)).ToArray()).Distinct(StringComparer.Ordinal).ToArray();
        var instructionEntries = skillComponents.Where(c => c.Id == PromptComponentId.SkillInstructions).ToArray();
        var activatedIds = (activatedSkillIds ?? instructionEntries.Select(c => ComponentSkillId(c.ContentReference)).ToArray()).Distinct(StringComparer.Ordinal).ToArray();
        var loadedIds = instructionEntries.Where(c => c.IsLoaded && textByContentReference.ContainsKey(c.ContentReference)).Select(c => ComponentSkillId(c.ContentReference)).Distinct(StringComparer.Ordinal).ToArray();
        var referenceLoaded = skillComponents.Where(c => c.Id == PromptComponentId.SkillReferences && c.IsLoaded && textByContentReference.ContainsKey(c.ContentReference))
            .Select(c => ComponentSkillId(c.ContentReference)).Distinct(StringComparer.Ordinal).Count();

        SkillCostMeasurement result;
        if (measured.MeasurementKind != "exact" || measured.Tokens is null || measured.Attribution is null)
        {
            result = new(SkillCostMeasurement.CurrentSchemaVersion, SkillCostMeasurement.ResultKind, "unavailable", null, null, null, null,
                availableIds.Length, consideredIds.Length, activatedIds.Length, loadedIds.Length, referenceLoaded, measured.UnavailableReason ?? "exact-attribution-unavailable",
                costs.Select(c => c with { Tokens = null, OmissionReason = c.OmissionReason ?? "exact-attribution-unavailable" }).ToArray())
            { InputMeasurement = measured };
        }
        else
        {
            var resultSkillTokens = checked(skillTokens);
            var overhead = measured.Attribution.Components.Where(c => c.Span.ComponentId == RenderedComponentSpan.Overhead).Sum(c => c.Tokens);
            var nonSkill = checked(measured.Tokens.Value - resultSkillTokens - overhead);
            result = new(SkillCostMeasurement.CurrentSchemaVersion, SkillCostMeasurement.ResultKind, "exact", measured.Tokens,
                resultSkillTokens, overhead, measured.Tokens.Value == 0 ? null : (double)resultSkillTokens / measured.Tokens.Value,
                availableIds.Length, consideredIds.Length, activatedIds.Length, loadedIds.Length, referenceLoaded, null, costs)
            { InputMeasurement = measured, NonSkillInputTokens = nonSkill };
        }
        result.Validate();
        return result;
    }

    private static string ComponentSkillId(string reference)
    {
        var slash = reference.IndexOf('/');
        return slash < 0 ? reference : reference[..slash];
    }
}
