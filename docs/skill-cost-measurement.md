# Skill cost measurement v2

`SkillCostMeasurementService.Measure` accepts the ordered `PromptManifest`, its
content-reference text, template identity/checksum, and optional discovery,
consideration, activation and omission observations. Use canonical skill IDs from
`SkillCompatibilityMapReader.ReadMetadataIndex`; skill content references have the
form `skill-id/version/resource`.

`SkillCostMeasurementService.Explain` consumes a `SkillCostExplainRequest` with
the final prompt/text projection, pinned tokenizer/template, canonical discovery
catalog (IDs and versions), considered IDs, and `SkillActivationLoadResult`.
It validates the catalog revision, role, tokenizer and load projection, and
available ⊇ considered ⊇ activated ⊇ instruction-loaded ⊇ reference-loaded
identity sets. Considered but inactive skills retain null instruction costs with
`skill-not-activated`; omitted loads retain their actual omission reasons.
The optional v2 `identities` field preserves these sets without private bodies.
Legacy measurement callers and v2 results without this field remain compatible.

`sdeveng skills explain --input <file>` uses the normal skills module/dispatch.
The input follows `schemas/skill-cost-explain-request.schema.json`; tokenizer
asset paths resolve relative to that file. Assets are verified by the existing
registry and no model runtime is invoked. Output is the schema-valid v2
measurement, including attributable IDs/costs/omissions and provenance, without
input text or load bodies. Worker invocation remains deferred.

The service uses `IRenderedInputTokenCounter.CountAttributed` with the same final
renderer and verified tokenizer as other production input measurements. Metadata
and loaded instruction costs are signed ordered-prefix deltas, never independent
fragment counts. Intentionally unloaded metadata/instructions are excluded from
the renderable manifest and retained as null-cost records with omission reasons.
Missing text for a component declared loaded makes the measurement unavailable.
Prompt manifest v1 remains readable and rejects loaded references; its resource
descriptor rendering is preserved. Manifest v2 permits loaded reference bodies
with canonical skill/version/resource identities and SHA256 hashes, checked by
the renderer before measurement. Unloaded v2 descriptors are excluded from input
and retain null costs with omission reasons. Missing loaded content is unavailable,
not zero. `MeasureLoadedReferences` consumes individually requested
`SkillActivationService.LoadReferenceAsync` results, checks the canonical inventory
resource hash, and projects their bodies into the same final-input measurement.
It reads no unrelated instruction bodies. Direct `Measure` callers supply the
equivalent verified manifest and content projection.

The result validates against `schemas/skill-cost-measurement.schema.json` and
retains the underlying input measurement, including template/tokenizer pins and
fixture provenance. `skillTokens` sums measured component deltas;
`templateOverheadTokens` sums actual template spans; `nonSkillInputTokens` contains
the remaining non-skill input (including legacy v1 descriptors).
The three categories reconcile to authoritative nonnegative `totalInputTokens`.
`tokenShare` is `skillTokens / totalInputTokens`, null for zero or unavailable
totals; a zero numerator with a positive total gives zero. Signed attribution can
produce negative costs or shares outside [0, 1]. `attributionSemantics` identifies
the ordered cumulative prefix algorithm; share is attribution, not probability.
Component records expose IDs,
content references, costs and omission reasons, never private instruction text.

Focused regression proof is `SkillCostMeasurementTests`; the registered offline
evaluation is `evals/skill-cost/eval.yaml`. Fixture tokenization proves this API's
behavior, not model qualification or inference.
