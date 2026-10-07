# Skill cost measurement v1

`SkillCostMeasurementService.Measure` accepts the ordered `PromptManifest`, its
content-reference text, template identity/checksum, and optional discovery,
consideration, activation and omission observations. Use canonical skill IDs from
`SkillCompatibilityMapReader.ReadMetadataIndex`; skill content references have the
form `skill-id/version/resource`. No CLI or worker invocation is added here.

The service uses `IRenderedInputTokenCounter.CountAttributed` with the same final
renderer and verified tokenizer as other production input measurements. Metadata
and loaded instruction costs are signed ordered-prefix deltas, never independent
fragment counts. Intentionally unloaded metadata/instructions are excluded from
the renderable manifest and retained as null-cost records with omission reasons.
Missing text for a component declared loaded makes the measurement unavailable.
Reference-body costs remain deferred; reference records have null costs with a
reason, without altering the existing rendering of resource descriptors.

The result validates against `schemas/skill-cost-measurement.schema.json` and
retains the underlying input measurement, including template/tokenizer pins and
fixture provenance. `skillTokens` sums measured component deltas;
`templateOverheadTokens` is the residual total (including non-skill input).
Together they reconcile to authoritative nonnegative `totalInputTokens`.
`tokenShare` is `skillTokens / totalInputTokens`, null for zero or unavailable
totals; a zero numerator with a positive total gives zero. Signed attribution can
produce negative costs or shares outside [0, 1]. Component records expose IDs,
content references, costs and omission reasons, never private instruction text.

Focused regression proof is `SkillCostMeasurementTests`; the registered offline
evaluation is `evals/skill-cost/eval.yaml`. Fixture tokenization proves this API's
behavior, not model qualification or inference.
