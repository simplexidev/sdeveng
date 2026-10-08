---
name: write-dotnet-tests
description: Add or revise focused .NET tests for a concrete behavior using the repository's established framework and conventions.
---

# Write .NET Tests

Inspect contract, callers, test project, and nearby tests. Use `sdeveng dotnet
inspect --json` for target frameworks and framework/package versions, then
`sdeveng dotnet test-plan --project <path> --json` for verification. Detected
facts and local patterns are authoritative; do not infer from SDK, target
framework, package names, or memory. Preserve versions, naming, fixtures,
assertion library, and data patterns. Do not scaffold/upgrade unless requested.

For symbol-specific C# requests, record source/test paths, target declaration,
and bounded ChangeSet first. Resolve against current Roslyn semantics when
available; reject stale/ambiguous targets, not textual-name matches. Preserve
API and behavior unless acceptance changes them. See [symbol-aware C# change
review](../architecture-change/references/symbol-aware-csharp.md) for anchor and
review evidence; this procedure guides targeting, not edits.

Map risk to `input/sequence -> outcome -> assertion`. Prefer the smallest case
that distinguishes correct behavior; cover meaningful negative/near boundaries.
Avoid implementation mirrors, broad snapshots, and tests proving only return.

Edit only needed tests. Run changed tests first via `run-dotnet-tests`, then
affected tests if narrow results are green and dependency evidence warrants it.
State what ran.

Read [`test-framework-edge-cases.md`](../../references/test-framework-edge-cases.md)
only for uncertainty about assertions, lifecycle, async/data-driven behavior, or
framework APIs. Skip when local patterns suffice. Code generation/API selection
use normal reasoning, not JEV.
