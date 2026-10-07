namespace SdevEng.Infrastructure;

/// <summary>Inventory-backed selection and measurement in an explicit, fixed prompt context.</summary>
public sealed class SkillProfileSelectionService(IRenderedInputTokenCounter tokenCounter)
{
    public SkillProfileSelection Select(string root, string identity, string modelFamily, string modelRevision, string role,
        SkillQualification? qualification, ChatTemplateManifest template, PromptManifest prompt,
        IReadOnlyDictionary<string, string> textByContentReference)
    {
        var canonical = SkillCompatibilityMapReader.ReadSelectionMetadata(root, identity);
        var body = canonical.Body!;
        var profile = SkillProfile.SelectProfile(canonical, modelFamily, modelRevision, role, canonical.Version!, qualification);
        SkillProfileSelection Fallback() => new(1, "skill-profile-selection", body, null, null, null, null);
        if (profile is null) return Fallback();
        template.Validate();
        prompt.Validate();
        if (template.Id != qualification!.TemplateId || template.Revision != qualification.TemplateRevision ||
            template.TokenizerId != qualification.TokenizerId || template.TokenizerRevision != qualification.TokenizerRevision)
            return Fallback();
        var reference = $"{canonical.Id}/{canonical.Version}/instructions";
        var component = prompt.Components.SingleOrDefault(c => c.Id == PromptComponentId.SkillInstructions && c.ContentReference == reference);
        if (component is null || !component.IsLoaded || component.Role != role)
            throw new ArgumentException("Profile measurement requires the named loaded skill instruction component.");
        RenderedInputTokenMeasurement Count(string content)
        {
            var text = new Dictionary<string, string>(textByContentReference, StringComparer.Ordinal) { [reference] = content };
            var hash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            var rendering = prompt with { Components = prompt.Components.Select(c => c == component ? c with { ContentHash = hash } : c).ToArray() };
            var result = tokenCounter.Count(template.Id, template.Revision, template.Checksum, rendering, text);
            result.Validate();
            return result;
        }
        var canonicalCost = Count(body);
        var profileCost = Count(profile.Body);
        if (canonicalCost.MeasurementKind != "exact" || profileCost.MeasurementKind != "exact") return Fallback();
        var selection = new SkillProfileSelection(1, "skill-profile-selection", profile.Body, qualification,
            checked(canonicalCost.Tokens - profileCost.Tokens), canonicalCost, profileCost);
        selection.Validate();
        return selection;
    }
}
