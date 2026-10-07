# Skill metadata index

`SkillCompatibilityMapReader.ReadSelectedSkill(root, identity, modelFamily,
modelRevision, role, qualification)` resolves a canonical ID or alias and loads
only that skill. The reader validates its versioned profile/qualification records.
An exact passing qualification (including tokenizer/template revisions and
structured safety, tools and required facts) selects the profile body. Missing
profiles or missing, failed or stale supplied evidence return the canonical body,
including its original whitespace. Savings never qualify a profile automatically.
Changed profile safety/tool/fact fields are rejected against qualification evidence
and canonical `safety`, `requiredTools` and `requiredFacts` metadata (absent fields
mean empty sets). Safety includes permission constraints. Callers supply the existing typed
qualification record; synthetic records prove selection only, not real-model
qualification. This callable boundary adds no worker workflow or CLI command.
Full metadata reads retain the body in memory but exclude it from serialization;
front-matter discovery still does not load instruction bodies.

`SkillProfileSelectionService.Select` is the callable measured selection boundary.
It uses the same inventory reader, an explicit registered template and prompt context,
and the existing `IRenderedInputTokenCounter`. Only the named instruction component
changes between canonical and profile renderings. Exact savings are the difference
between full rendered token totals, with both measurements and matching qualification
included in the version 1 `skill-profile-selection` result. Its validator checks pins,
asset identity and arithmetic; the schema is `schemas/skill-profile-selection.schema.json`.
Stale runtime pins or unavailable exact counting return canonical content without
savings. A measured difference inconsistent with qualified savings is rejected.
Synthetic tokenizer evidence remains explicitly fixture-only and does not establish
real-model qualification. The existing body-only reader remains compatible.

`SkillCompatibilityMapReader.ReadMetadataIndex(root)` indexes the unified plugin's
skill front matter. It stops reading each skill at the closing `---` delimiter,
parses resource declarations without opening or hashing resources, and projects
legacy missing IDs/versions through the validated compatibility map. Canonical
IDs and aliases share the map's identity namespace; colliding resolved skill IDs
are rejected. Malformed metadata fails the operation rather than being omitted.

`PluginManifests.Validate`, called by `Validation.Run` and the existing validate
command, invokes the index and checks required tools against
`config/agent-tool-contracts.json`. Explicit validation also retains the deep
`Read`/`ReadSkillMetadata` resource existence and hash checks.

`skills list --json` delegates through normal command-module discovery to the same
index and tool validation. It checks resource ownership and existence without
reading resource contents, and emits compact metadata plus structured diagnostics
under `schemas/skill-catalog.schema.json`. Declared roles must be unique members of
planner, coder, test-author, reviewer or repair; model/budget mapping is deferred
until the role registry exists. Explicit validation continues to verify hashes.

Deterministic regression cases in `SkillCompatibilityMapTests` cover the exact
closing-delimiter read boundary, legacy projection with an unavailable resource
and large instruction body, duplicate resolved identities through the index and
validation caller, and unknown-tool diagnostics through `Validation.Run`.

`SkillActivationService.Activate(root, context)` is the callable deterministic
activation boundary over this catalog. Callers supply a declared role key,
requested capability/tool IDs, available tool IDs and exact framework versions.
Activation conditions are alternatives: an unversioned ID matches a requested
capability, an available requested tool, or a framework ID; a versioned condition
matches only that framework and exact version. Role restrictions and all required
tools filter candidates before ordering. Exact framework matches have priority 2,
other matches priority 1; ordinal canonical ID resolves equal priority. Skills
without matching declarations remain inactive. Unknown roles and ambiguous
framework facts fail the operation. The operation returns existing metadata only;
it never loads instructions or resources. Budgeted loading uses the entry point below.

`SkillActivationService.ActivateAsync(root, context)` adds the optional
`ISkillSemanticTieBreakProvider` seam, defaulting to abstention without external
calls. It offers only eligible, equal-priority groups of 2–25 candidates, with
canonical IDs as text and the declared role as query; instruction bodies and
references are never supplied. Larger groups retain deterministic order without
provider calls. Existing `RelevanceRankingOrder` score/confidence validation
(default minimum confidence 0.80) rejects invalid, duplicate, unknown or uncertain
scores. Accepted scores can reorder a tie but cannot alter eligibility or priority.
Missing or below-threshold scores preserve ordinal ID order. Synchronous callers
retain deterministic activation. Local JEV binding remains deferred.

`SkillActivationTests.CallableCatalogBoundaryFiltersAndOrdersWithoutLoadingContent`
provides fixture evaluation of eligibility, equal-priority order, exact versions,
required tools, repeated deterministic results and malformed caller facts through
the real catalog-to-operation boundary, with unavailable references and large bodies.
The same fixture evaluates async default abstention, below-threshold abstention,
accepted tie-breaking, rejection of ineligible provider IDs and oversized groups
that abstain before any provider call. Returned metadata retains schema validation.

`SkillActivationService.LoadInstructionsAsync(root, context, canonicalId)` and
`LoadReferenceAsync(root, context, canonicalId, referencePath)` connect the same
async selector directly to lazy content loading. Each call rechecks activation;
inactive or unknown IDs return `skill-not-activated`. Instruction loading returns
only the selected body's text after front matter. Reference loading requires one
exact declared resource of type `reference`, validates its owned path without
symbolic links, and verifies its declared SHA-256 before returning content. It
never opens other references or follows links mentioned in content. Unknown
references, unsafe paths, missing content and hash mismatches return explicit
omissions without content. Cancellation propagates; malformed catalog metadata
still fails discovery. No CLI or worker workflow is introduced.

`SkillLoadResult` records loaded/omitted status, content or omission reason, UTF-8
byte count and .NET character count, under `schemas/skill-load-result.schema.json`.
The individual load methods remain compatible unbudgeted primitives.
`SkillLazyLoadingTests` evaluates the real
selector-to-loader boundary with unavailable unrelated references, role exclusion,
default abstention order, individual body/reference loads, unknown and non-reference
resources, traversal, links, tampering, cancellation and result schema checks.

`SkillActivationService.ActivateAndLoadAsync(root, context, tokenizers, tokenizerId,
references, modelContextTokens)` is the budgeted activation/load boundary. It reads
`config/context-budget-policy.json` through the canonical budget service, applies
the current role's input override and skills share, and resolves an explicitly
registered, verified tokenizer. Missing or unsupported encoding fails closed;
there is no estimated token fallback. Activation runs once with the same bounded
semantic seam and default abstention. Instruction bodies are admitted in activation
order, followed by at most 1024 unique, explicitly requested individual references
in caller order. References require an accepted instruction body. No recursive
reference loading or CLI/worker workflow is added.

The allowance covers the sum of exact, individually encoded accepted instruction
and reference contents, excluding front matter; it is not a rendered chat-input
measurement. Over-budget content is discarded with `skills-budget-exceeded`
evidence and its measured token cost, without consuming budget. A reference whose
body was omitted returns `instructions-not-loaded`; unknown references remain
explicit omissions. Other loader safety/hash/availability checks are preserved.

`SkillActivationLoadResult` records caller role/WorkUnit facts, selected canonical
IDs in order, effective skill allowance, complete tokenizer attribution (including
fixture-only provenance), accepted/omitted load evidence, measured tokens and
consumed/remaining totals. Its owning validator checks admission order, reference
prerequisites, content byte/character counts and budget arithmetic before return;
`schemas/skill-activation-load-result.schema.json` checks serialized shape. Evidence
is returned to the caller, without adding a persistence store.

The `SkillLazyLoadingTests` fixture evaluation calls this entry point to prove
role exclusion, default abstention order, exact-fit instruction admission,
cumulative body/reference overflow, references omitted with their bodies,
unknown references, per-role allowances, repeated deterministic evidence,
cancellation, unavailable tokenizer rejection, invalid model context and duplicate
requests. It validates result schemas and rejects inconsistent evidence totals.
The byte-tokenizer fixture proves this boundary only, not production-model
qualification or full worker prompt admission.
