using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Measures skill component costs against the same rendered input and tokenizer used by production.</summary>
public sealed class SkillCostMeasurementService(IRenderedInputTokenCounter tokenCounter)
{
    /// <summary>Projects individually authorized lazy-load results using canonical resource identities and hashes.</summary>
    public SkillCostMeasurement MeasureLoadedReferences(string toolkitRoot, string templateId, string templateRevision,
        string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference,
        IReadOnlyList<SkillLoadResult> referenceLoads)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(textByContentReference);
        ArgumentNullException.ThrowIfNull(referenceLoads);
        prompt.Validate();
        if (prompt.SchemaVersion != 2) throw new ArgumentException("Reference loads require prompt manifest v2.");
        var inventory = SkillCompatibilityMapReader.ReadMetadataIndex(toolkitRoot);
        var text = new Dictionary<string, string>(textByContentReference, StringComparer.Ordinal);
        var components = prompt.Components.ToList();
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var load in referenceLoads)
        {
            var skill = inventory.Single(s => s.Id == load.SkillId);
            var resource = (skill.Resources ?? []).Single(r => r.Type == "reference" && r.Path == load.ReferencePath);
            var reference = $"{skill.Id}/{skill.Version}/{resource.Path}";
            if (reasons.ContainsKey(reference)) throw new ArgumentException("Duplicate reference load.");
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
                reasons.Add(reference, "loaded");
            }
            else
            {
                if (load.Status != "omitted" || load.Content is not null || load.Utf8Bytes != 0 || load.Characters != 0 || string.IsNullOrWhiteSpace(load.OmissionReason))
                    throw new ArgumentException("Invalid omitted reference load.");
                components[index] = components[index] with { IsLoaded = false };
                reasons.Add(reference, load.OmissionReason);
            }
        }
        return Measure(templateId, templateRevision, templateChecksum, prompt with { Components = components }, text, reasons);
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
