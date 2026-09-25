# Changelog

## 3.0.0

- Rename the active product identity to SimplexiDev Engineering Toolkit (`sdeveng`).
- Preserve v2 installation manifests, `CODEX_TOOLKIT_ROOT`, and `codex-agent-tool` as migration aliases.
- Make `sdeveng` the canonical CLI with version reporting, a schema-versioned `--json`
  result envelope, documented exit codes, and structured skill invocations.

## 2.0.0

- Adds conservative per-capability JEV call budgets, deterministic-first and privacy
  gates, GPT escalation, and payload-free routing instrumentation.
- Adds a schema-validated agent audit and metrics handoff covering current, retired,
  native-comparator, and proposed roles without installing new production agents.
- Adds bounded GitHub Actions run/job/failed-log inspection with one compact CI
  triage skill, while keeping reruns and workflow mutations caller-controlled.
- Adds non-merging conflict forecasts, SARIF baseline diffs, ZIP safety/content
  inspection, exact SHA-256 verification, and evidence-backed hygiene candidates.
- Consolidates the new evidence into existing PR, security, release, and repository
  health skills instead of adding overlapping workflow skills.
- Adds versioned AgentTool contracts for bounded repository/change summaries, file
  ownership and impact, .NET environment/project inspection, resolved dependencies,
  and non-executing build, test, binlog, and diagnostics plans.
- Normalizes TRX/JUnit test results and Cobertura/OpenCover coverage into compact JSON.
- Replaces overlapping test guidance with four intent-specific skills for running,
  writing, quality review, and coverage; platform/framework edge cases load lazily.
- Makes test plans detect VSTest/MTP command mode and framework, translate method,
  class, or category scopes, and fail closed for ambiguous platform filters.
- Adds compact build-diagnosis, build-optimization, runtime-diagnosis, and .NET
  performance skills with lazy MSBuild and advanced-.NET routing references.
- Makes diagnostics plans select one bounded signal and report OS, architecture,
  runtime, SDK, tool availability, artifact location, and collection safety.

## 1.0.0

First stable release of the Codex toolkit.

- Provides 24 focused skills, one bounded read-only reviewer agent, and a dependency-free
  .NET 10 file-based CLI for repository, validation, release, and result-store workflows.
- Supports isolated, ownership-aware install, update, doctor, and uninstall lifecycles.
- Adds conservative TypeSafe/JEV Noul, Choice, Score, and screening flows with fake-response
  validation, bounded caching, credential confinement, and output redaction.
- Adds schema/plugin validation, offline skill regression fixtures, cross-platform CI,
  release packaging, upstream-drift reporting, and durable audit evidence.
- Resolves the structural, credential-boundary, installer-reconciliation, Windows CI,
  symlink-write, malformed-response, and token-efficiency findings found during v1 review.

## 0.1.0

Initial toolkit: deterministic repository/.NET commands, safe symlink lifecycle,
conservative JEV client, 25 skills, native agents, tests, evaluations and release metadata.
