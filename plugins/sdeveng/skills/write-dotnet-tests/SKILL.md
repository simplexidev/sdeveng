---
name: write-dotnet-tests
description: Add or revise focused .NET tests for a concrete behavior using the repository's established framework and conventions.
---

# Write .NET Tests

Inspect the production contract, its callers, the test project, and a few nearby
tests. Use `sdeveng dotnet inspect --json` for project/framework facts and
`sdeveng dotnet test-plan --project <path> --json` for the verification command. Preserve the
installed framework, package versions, naming, fixtures, assertion library, and
data patterns; do not scaffold or upgrade a test project unless requested.

Translate the requested risk into `input or sequence -> observable outcome ->
expected assertion`. Prefer the smallest case that distinguishes correct from
incorrect behavior. Cover a relevant negative path or nearest boundary when it
materially changes the contract; avoid implementation mirrors, broad snapshots,
and tests that only prove the method returned.

Edit only the tests needed for the behavior. Run the new or changed test first
through `run-dotnet-tests`, then affected tests when the narrow result is green
and dependency evidence justifies it. State what ran.

Read [`test-framework-edge-cases.md`](../../references/test-framework-edge-cases.md)
only when an assertion, lifecycle, async, data-driven, or framework-version API
is uncertain. Do not load it for ordinary tests whose repository pattern is
already clear. Code generation and API selection require normal reasoning, not
JEV.
