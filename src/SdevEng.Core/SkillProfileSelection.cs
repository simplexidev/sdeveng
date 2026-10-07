namespace SdevEng;

public sealed record SkillQualification(string ModelFamily, string ModelRevision, string Role, string SkillVersion,
    bool Passed, long TokensSaved, string TokenizerId, string TokenizerRevision, string TemplateId, string TemplateRevision,
    string[] Safety, string[] RequiredTools, string[] RequiredFacts)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelFamily) || string.IsNullOrWhiteSpace(ModelRevision) ||
            Role is not ("planner" or "coder" or "test-author" or "reviewer" or "repair") ||
            !System.Text.RegularExpressions.Regex.IsMatch(SkillVersion ?? "", "^\\d+\\.\\d+\\.\\d+$") ||
            TokensSaved < 0 || new[] { TokenizerId, TokenizerRevision, TemplateId, TemplateRevision }.Any(string.IsNullOrWhiteSpace) ||
            Safety is null || RequiredTools is null || RequiredFacts is null ||
            new[] { Safety, RequiredTools, RequiredFacts }.Any(values => values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length))
            throw new ArgumentException("Invalid skill qualification record.");
    }
}

/// <summary>Savings for the selected body in one fixed rendering context; evidence never auto-qualifies a profile.</summary>
public sealed record SkillProfileSelection(int SchemaVersion, string Kind, string Body, SkillQualification? Qualification,
    long? TokensSaved, RenderedInputTokenMeasurement? CanonicalMeasurement, RenderedInputTokenMeasurement? ProfileMeasurement)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || Kind != "skill-profile-selection" || Body is null)
            throw new ArgumentException("Invalid skill profile selection.");
        if (Qualification is null)
        {
            if (TokensSaved is not null || CanonicalMeasurement is not null || ProfileMeasurement is not null)
                throw new ArgumentException("Canonical fallback cannot report qualified savings.");
            return;
        }
        Qualification.Validate();
        ArgumentNullException.ThrowIfNull(CanonicalMeasurement);
        ArgumentNullException.ThrowIfNull(ProfileMeasurement);
        CanonicalMeasurement.Validate();
        ProfileMeasurement.Validate();
        var left = CanonicalMeasurement;
        var right = ProfileMeasurement;
        if (!Qualification.Passed || left.MeasurementKind != "exact" || right.MeasurementKind != "exact" ||
            left.TemplateId != Qualification.TemplateId || left.TemplateRevision != Qualification.TemplateRevision ||
            left.TokenizerId != Qualification.TokenizerId || left.TokenizerRevision != Qualification.TokenizerRevision ||
            left.TemplateId != right.TemplateId || left.TemplateRevision != right.TemplateRevision || left.TemplateChecksum != right.TemplateChecksum ||
            left.TokenizerId != right.TokenizerId || left.TokenizerRevision != right.TokenizerRevision ||
            left.Method != right.Method || left.FixtureOnly != right.FixtureOnly || !left.TokenizerAssets.SequenceEqual(right.TokenizerAssets) ||
            TokensSaved != checked(left.Tokens - right.Tokens) || TokensSaved != Qualification.TokensSaved)
            throw new ArgumentException("Profile savings do not match pinned qualification evidence.");
    }
}
