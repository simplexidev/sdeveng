using SdevEng;

namespace SdevEng.Infrastructure;

/// <summary>Measures skill component costs against the same rendered input and tokenizer used by production.</summary>
public sealed class SkillCostMeasurementService(IRenderedInputTokenCounter tokenCounter)
{
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
            c.Id is not (PromptComponentId.SkillMetadata or PromptComponentId.SkillInstructions)).ToArray()
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
            if (component.Id == PromptComponentId.SkillReferences || !component.IsLoaded || !textByContentReference.ContainsKey(component.ContentReference))
            {
                costs.Add(new(ComponentSkillId(component.ContentReference), kind, component.ContentReference, null,
                    omissionReasons.GetValueOrDefault(component.ContentReference, component.Id == PromptComponentId.SkillReferences ? "reference-cost-deferred" : "content-not-loaded")));
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
            var nonSkillAndTemplate = checked(measured.Tokens.Value - resultSkillTokens);
            result = new(SkillCostMeasurement.CurrentSchemaVersion, SkillCostMeasurement.ResultKind, "exact", measured.Tokens,
                resultSkillTokens, nonSkillAndTemplate, measured.Tokens.Value == 0 ? null : (double)resultSkillTokens / measured.Tokens.Value,
                availableIds.Length, consideredIds.Length, activatedIds.Length, loadedIds.Length, referenceLoaded, null, costs)
            { InputMeasurement = measured };
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
