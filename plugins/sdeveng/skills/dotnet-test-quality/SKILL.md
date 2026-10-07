---
name: dotnet-test-quality
description: Review scoped .NET test adequacy, behavioral gaps, false passes, flakiness, and assertion reliability.
---

# .NET Test Quality

Set scope from the named behavior, changed tests, or affected project graph.
Inspect production outcomes and existing assertions together. When execution is
useful, obtain one targeted baseline from `run-dotnet-tests`; do not start with a
solution-wide run, mutation testing, or coverage collection.

Before making framework-specific claims, use `sdeveng dotnet inspect --project
<test-project> --json` and rely on its detected test framework and package
versions. Do not infer assertion APIs from the SDK or target framework. If the
framework/version is unknown or unsupported, say which fact is unavailable and
avoid prescribing version-specific syntax; load the framework edge-case
reference only when that uncertainty affects the review.

Build a compact ledger: `public behavior -> witness -> expected observation ->
existing assertion -> gap`. Prioritize silent false passes, security denials,
state/side effects, error behavior, and adjacent boundaries. Calibrate framework
idioms: exception assertions, mock verification, snapshots, and data-driven
cases can be real assertions. A missing direct assertion is not a gap when an
existing public-path assertion already observes the outcome.

For a failing test, inspect its exact assertion, fixture inputs, production
output, and caller. Classify the evidence as a production defect, incorrect
expectation, fixture/harness defect, platform-specific test infrastructure
failure, or nondeterministic construction. Repair production behavior when a
correct assertion exposes a contract defect; preserve correct assertions and
never weaken them to make a run pass. Distinguish a failed or unavailable check
from a passing check, and keep recommendations within the selected project or
affected test scope.

Read [`test-quality-checks.md`](../../references/test-quality-checks.md) only for
an explicit quality audit or when candidate classification is unclear. Mutation
tools and comprehensive suite audits require explicit request or material
evidence.

For more than ten sanitized, evidence-backed candidates, JEV capability
`relevance` with purpose `test-gap-ranking` may screen or rank
relevance; retain file/behavior identifiers and review uncertain results.
Never ask JEV to decide correctness, test outcomes, framework facts, or final
adequacy. Report only supported findings, ordered by risk, with one concrete
fix and targeted verification per finding.
