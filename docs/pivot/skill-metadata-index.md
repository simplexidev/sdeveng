# Skill metadata index

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
it never loads instructions or resources. Semantic tie-breaking and budgeted lazy
loading remain separate later operations; this selector uses deterministic order.

`SkillActivationTests.CallableCatalogBoundaryFiltersAndOrdersWithoutLoadingContent`
provides fixture evaluation of eligibility, equal-priority order, exact versions,
required tools, repeated deterministic results and malformed caller facts through
the real catalog-to-operation boundary, with unavailable references and large bodies.
