---
name: dotnet-coverage
description: Collect or interpret targeted .NET coverage evidence without treating percentage alone as test quality.
---

# .NET Coverage

Use only for supplied evidence or an explicit coverage request. Summarize an
existing Cobertura/OpenCover file first with `sdeveng coverage summarize --file PATH --json`;
never rerun tests or generate a report just to restate it.

For collection, use `sdeveng dotnet test-plan --json` for the smallest scope,
SDK, target frameworks and test platform; use
`sdeveng dotnet inspect --project PATH --json` for provider/package versions.
Use established repository commands and detected versions only. State unknown
or unsupported SDK/project/platform/provider/version; do not guess flags or
add/upgrade packages. If syntax is unestablished, read
[`test-platform-edge-cases.md`](../../references/test-platform-edge-cases.md)
and stop. Distinguish failed, unsupported or unavailable collection from
success; test pass/exit without a report is not proof.

Treat counters as exact measurements: reconcile covered and valid totals and
identify the denominator. Lines prove execution, not branch outcomes or
discriminating assertions. Relate gaps to behavior; check test framework and
package versions before suggesting assertions. Mutation/CRAP is opt-in.

JEV capability `relevance` with purpose `coverage-gap-ranking` may rank a large
sanitized list of already-computed candidate gaps. It must
not parse reports, calculate percentages, set thresholds, or decide whether a
behavior is adequately tested. Keep raw reports local and pass only bounded
summaries to models.
