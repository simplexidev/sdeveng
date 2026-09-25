# Capability coverage engineering reference

This is a compact runtime reference for agents and metrics consumers, not the
human product manual. The source of record is the versioned
[capability manifest](../../../config/capabilities.json), validated by its
[JSON schema](../../../schemas/capabilities.schema.json).

## Audit result

The audit inventories 31 common repository/workspace capability areas: 12 are
supported, 18 are partial, and 1 is a gap. Coverage means the toolkit has the
correct routing and a useful workflow; it does not imply every platform mutation
is automated. Commit, push, PR merge, publication, external tool installation,
and destructive cleanup remain authority boundaries.

The default routing order is deterministic tooling, bounded JEV judgment, normal
agent reasoning, then stronger reasoning only for unusually consequential or
difficult analysis. JEV is suitable only for sanitized ambiguous candidate sets;
it is not a substitute for Git state, compiler/analyzer output, advisories,
measurements, package identity, licenses, security findings, or authorization.

Official .NET skill routing and pinned source provenance are recorded separately
in [dotnet-skills-provenance.md](dotnet-skills-provenance.md). That inventory is
complete at its pinned SHA but remains lazy routing metadata, not default context.

## Portfolio findings

| Area | Finding | Engineering direction |
| --- | --- | --- |
| Repository, Git, .NET, tests | Strong structured core | Keep exact discovery and validation in AgentTool; use skills for narrow workflow policy. |
| GitHub | PR/review and bounded Actions run/job/failed-log diagnosis exist | Keep platform mutations caller-controlled and measure whether remote artifact retrieval is needed. |
| Supply chain | Vulnerability checks and generic ZIP inspection exist; resolved license compliance and package-specific metadata do not | Define machine-readable license policies before compliance guidance and add package rules only from stable schemas. |
| Build/release | Broad release checks plus local artifact inspection/hash verification exist; reproducibility, SBOM and remote retrieval remain separate | Emit composable evidence and eventually a release attestation rather than a monolithic command. |
| Maintenance | Upstream and plugin lifecycle are bounded and conservative | Move evaluation ownership to metrics in its own phase; let metrics ingest this manifest. |
| Repository hygiene | Tracked generated/output candidates are evidence-backed; reachability remains ecosystem-specific | Add graph-based orphan analysis only where exact consumers are available; never auto-delete. |

## Recommended sequence

1. Define resolved dependency license policy and inventory schemas before adding
   any compliance workflow guidance; license ambiguity remains the sole gap.
2. Add opt-in reproducibility and remote release-asset verification, then compose
   their results into a machine-readable release attestation.
3. Add ecosystem-specific generated/orphan reachability only when provenance and
   consumer evidence can be explained per item.

The testing optimization adds four narrow skills because measured upstream testing
context did not improve baseline correctness and materially increased cost. Their
overlap is intentional composition: `run-dotnet-tests` executes evidence,
`write-dotnet-tests` authors cases, `dotnet-test-quality` assesses design, and
`dotnet-coverage` interprets counters. Elsewhere, `dependency-change` consumes
`package-audit`; `diagnose-dotnet` selects incident evidence while
`investigate-dotnet-performance` reasons about measured regressions; and release verification
orchestrates but does not claim optional API, SBOM, packaging, or reproducibility
gates. Metrics should track activation precision, static routing cost, unnecessary
broad operations, fallback rate, and correctness by capability ID.

## Deterministic expansion

The first post-audit expansion adds versioned structured contracts for repository
summaries and file ownership; .NET SDK, TFM, package and project-graph inspection;
resolved direct/transitive dependencies; non-executing build, test and diagnostics
plans; and bounded TRX, JUnit, Cobertura and OpenCover summaries.
`config/agent-tool-contracts.json` is the compact source
for metrics consumers. Plans expose executable argument arrays but do not cross
the existing mutation or sensitive diagnostics-collection boundaries.

The general-capability expansion adds bounded GitHub Actions run/job/failed-log
inspection, merge-tree conflict forecasts, SARIF baseline diffs, ZIP safety and
SHA-256 evidence, and tracked hygiene candidates. These commands do not rerun
jobs, merge branches, extract archives or delete candidates. A single compact
`ci-triage` skill is the only new routing surface; release, security, PR and
repository-health workflows reuse their existing skills.
