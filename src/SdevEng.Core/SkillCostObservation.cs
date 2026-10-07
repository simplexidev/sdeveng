namespace SdevEng;

public sealed record SkillCatalogIdentity(string Id, string Version);

/// <summary>Discovery and consideration evidence for the same role and catalog as activation.</summary>
public sealed record SkillCostObservation(string Role, IReadOnlyList<SkillCatalogIdentity> Catalog,
    IReadOnlyList<string> ConsideredSkillIds, SkillActivationLoadResult Activation);

/// <summary>Replayable final-input measurement request; contents are never included in explain output.</summary>
public sealed record SkillCostExplainRequest(PromptManifest Prompt,
    IReadOnlyDictionary<string, string> TextByContentReference, SkillCostObservation Observation,
    TokenizerManifest Tokenizer, ChatTemplateManifest Template);
