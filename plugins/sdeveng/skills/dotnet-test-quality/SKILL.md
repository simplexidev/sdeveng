---
name: dotnet-test-quality
description: Review scoped .NET test adequacy, behavioral gaps, false passes, flakiness, and assertion reliability.
---

# .NET Test Quality

Scope to named behavior, changed tests, or affected project graph. Inspect
production outcomes and assertions together. If useful, obtain one targeted
baseline via `run-dotnet-tests`; avoid solution-wide runs, mutation testing, and
coverage collection.

Before framework-specific claims, use `sdeveng dotnet inspect --project
<test-project> --json`; rely on detected framework/package versions, not SDK or
target framework. If unknown/unsupported, name the missing fact and avoid
version-specific syntax. Load the framework edge reference only if needed.

Build a ledger: `behavior -> witness -> observation -> assertion -> gap`.
Prioritize false passes, denials, state/effects, errors, and nearby boundaries.
Exception assertions, mock verification, snapshots, and data-driven cases can
be valid; no direct assertion is needed when a public-path assertion observes
the outcome.

For failures inspect assertion, fixture, production output, and caller. Classify
production defects, wrong expectations, fixture/harness defects, platform
failures, or nondeterminism. Fix production when a correct assertion exposes a
contract defect; never weaken it. Distinguish failed/unavailable checks from
passes and keep recommendations in scope.

Read [`test-quality-checks.md`](../../references/test-quality-checks.md) only
for explicit audits or unclear candidates. Mutation/comprehensive audits need
request or material evidence.

For >10 sanitized, evidence-backed candidates, JEV `relevance` with purpose
`test-gap-ranking` may rank them; retain identifiers and review uncertainty.
JEV cannot decide correctness, outcomes, framework facts, or adequacy. Report
supported findings by risk, each with a fix and targeted verification.
