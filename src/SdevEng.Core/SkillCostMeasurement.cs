namespace SdevEng;

public sealed record SkillComponentTokenCost(string SkillId, string ComponentKind, string ContentReference, long? Tokens, string? OmissionReason);

/// <summary>Versioned, attributable view of skill tokens within the authoritative final rendered input.</summary>
public sealed record SkillCostMeasurement(int SchemaVersion, string Kind, string MeasurementKind,
    long? TotalInputTokens, long? SkillTokens, long? TemplateOverheadTokens, double? TokenShare,
    int AvailableSkills, int ConsideredSkills, int ActivatedSkills, int InstructionLoadedSkills,
    int ReferenceLoadedSkills, string? UnavailableReason, IReadOnlyList<SkillComponentTokenCost> Components)
{
    public required RenderedInputTokenMeasurement InputMeasurement { get; init; }
    public long? NonSkillInputTokens { get; init; }
    public string AttributionSemantics { get; init; } = OrderedComponentAttribution.AlgorithmVersion;
    public const int CurrentSchemaVersion = 2;
    public const string ResultKind = "skill-cost-measurement";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(InputMeasurement);
        InputMeasurement.Validate();
        if (SchemaVersion != CurrentSchemaVersion || Kind != ResultKind || AttributionSemantics != OrderedComponentAttribution.AlgorithmVersion ||
            MeasurementKind is not ("exact" or "unavailable") || AvailableSkills < 0 || ConsideredSkills < 0 ||
            ActivatedSkills < 0 || InstructionLoadedSkills < 0 || ReferenceLoadedSkills < 0 ||
            InstructionLoadedSkills > ActivatedSkills || Components is null ||
            Components.Any(c => c is null || string.IsNullOrWhiteSpace(c.SkillId) ||
                c.ComponentKind is not ("metadata" or "instructions" or "reference") || string.IsNullOrWhiteSpace(c.ContentReference) ||
                (c.Tokens is null ? string.IsNullOrWhiteSpace(c.OmissionReason) : c.OmissionReason is not null)))
            throw new ArgumentException("Invalid skill cost measurement.");
        if (MeasurementKind == "unavailable")
        {
            if (TotalInputTokens is not null || SkillTokens is not null || TemplateOverheadTokens is not null || NonSkillInputTokens is not null ||
                TokenShare is not null || string.IsNullOrWhiteSpace(UnavailableReason) ||
                Components.Any(c => c.Tokens is not null)) throw new ArgumentException("Invalid unavailable skill cost measurement.");
            return;
        }
        if (InputMeasurement.MeasurementKind != "exact" || InputMeasurement.Attribution is null || InputMeasurement.Tokens != TotalInputTokens ||
            TotalInputTokens is null or < 0 || SkillTokens is null || TemplateOverheadTokens is null || NonSkillInputTokens is null ||
            (TotalInputTokens > 0 && (TokenShare is null || !double.IsFinite(TokenShare.Value))) || (TotalInputTokens == 0 && TokenShare is not null) || UnavailableReason is not null ||
            checked(SkillTokens + TemplateOverheadTokens + NonSkillInputTokens) != TotalInputTokens ||
            InputMeasurement.Attribution.Components.Where(c => c.Span.ComponentId == RenderedComponentSpan.Overhead).Sum(c => c.Tokens) != TemplateOverheadTokens ||
            InputMeasurement.Attribution.Components.Where(c => c.Span.ComponentId != RenderedComponentSpan.Overhead &&
                !Components.Any(cost => cost.Tokens is not null && cost.ContentReference == c.Span.ContentReference &&
                    c.Span.ComponentId == (cost.ComponentKind switch { "metadata" => "skill-metadata", "instructions" => "skill-instructions", _ => "skill-references" }))).Sum(c => c.Tokens) != NonSkillInputTokens ||
            Components.Aggregate(0L, (sum, c) => checked(sum + (c.Tokens ?? 0))) != SkillTokens ||
            (TotalInputTokens > 0 && TokenShare != (double)SkillTokens / TotalInputTokens))
            throw new ArgumentException("Invalid exact skill cost measurement.");
        foreach (var cost in Components.Where(c => c.Tokens is not null))
        {
            var wireKind = cost.ComponentKind switch { "metadata" => "skill-metadata", "instructions" => "skill-instructions", _ => "skill-references" };
            var deltas = InputMeasurement.Attribution.Components.Where(c => c.Span.ComponentId == wireKind && c.Span.ContentReference == cost.ContentReference).ToArray();
            if (deltas.Length == 0 || deltas.Aggregate(0L, (sum, c) => checked(sum + c.Tokens)) != cost.Tokens)
                throw new ArgumentException("Skill cost does not match input attribution.");
        }
    }
}
