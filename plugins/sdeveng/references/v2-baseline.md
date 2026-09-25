# v2 product baseline

This compact runtime reference anchors the v2 integration line at the `v1.0.0`
release. The machine-readable ownership contract is `config/ecosystem.json`.
Routing and metrics consumers use the versioned capability inventory in
`config/capabilities.json`; the compact audit interpretation is
`references/capability-coverage.md`.

## Ecosystem boundary

- `simplexidev/codex-toolkit` owns the runtime product: one unified plugin at
  `plugins/codex-toolkit`, one project template bundle at `templates/project`,
  AgentTool, runtime skills/references, custom agents, JEV integration, lifecycle
  operations, product tests, and releases.
- `simplexidev/codex-toolkit-docs` owns human documentation. It is not a runtime
  dependency.
- `simplexidev/codex-toolkit-metrics` owns evaluator, metrics, sanitized versioned
  metrics data, dashboards, and Pages at
  `https://simplexidev.github.io/codex-toolkit-metrics/`. It is not a runtime dependency.
- Agent/runtime-consumed references remain in the product repository even when the
  documentation repository separately explains the same topic for humans.

## Release inventory

- Skills (24): address-pr-review, agent-maintenance, api-compatibility,
  architecture-change, benchmark, dependency-change, diagnostics, docs-impact,
  dotnet-format, dotnet-verify, finish-pr, issue-start, jev-judgment,
  package-audit, performance-investigation, prepare-commit, release-verify,
  repo-health, reproducible-build, roadmap-next, sbom, security-scan,
  test-quality, and versioning. Each has narrow activation metadata and an eval fixture.
- Custom agents (1): `reviewer`, a read-only, evidence-backed reviewer for requested or
  substantial high-risk diffs. Delegation is root-only: this reviewer never delegates.
- AgentTool command groups (37 commands): lifecycle (`install`, `update`, `uninstall`,
  `doctor`); Git/GitHub (`git state`, `git prepare-commit`, `git issue-start`,
  `github prepare-pr`, `github pr-status`, `github review-comments`); repository
  discovery (`repo changed-files`, `repo locate`, `repo affected-projects`,
  `repo health`); .NET (`dotnet verify`, `dotnet format`, `dotnet package-audit`,
  `dotnet api-check`, `dotnet release-verify`); bounded output (`logs summarize`,
  `sarif summarize`); JEV (`jev noul`, `jev choice`, `jev score`, `jev screen`,
  `jev cache-clear`); upstream (`upstream status`, `upstream update`); product gates
  (`validate`, `eval`, `release`); and durable results (`results init`, `results new`,
  `results list`, `results latest`, `results context`, `results clean`).
- Installer topology: owned symlinks connect global instructions, the native agent,
  and each skill to a selected home/Codex home. Unix may add an AgentTool bin link.
  An ownership manifest enables idempotent update and conservative uninstall while
  preserving user replacements. The plugin may instead be registered from its local
  marketplace; duplicate skill discovery should be avoided.
- JEV behavior: optional bounded Noul/Choice/Score judgments and per-candidate screening
  follow deterministic narrowing. Inputs require explicit review before transmission;
  malformed, uncertain, unavailable, or invalid responses fall back to REVIEW/Codex.
  The API key is process-scoped and never persisted. Cache entries contain responses,
  not requests or credentials. Normal tests and CI use fake HTTP responses.
- Tests: 19 focused suites cover CLI/configuration, credentials, evaluation, Git,
  installation, JEV routing/client/cache/responses, metadata, output compaction,
  discovery, repository health, durable results, and safe files. Validation also checks
  schemas, mirrored plugin metadata, skill/eval/UI structure, agent metadata, and release
  identity.
- Upstream content: no third-party implementation is vendored. Manifests reference
  selected `dotnet/skills` capabilities and optional .NET tools; drift checks report for
  review and never download, install, edit manifests, or merge automatically.

## Known v1 baseline limitations

- Human-oriented documents and evaluation scenarios remain co-located from v1 and must
  move to their owning sibling repositories in later v2 phases after consumer checks.
- Optional tool installation, full binlog interpretation, project-specific SBOM/API/
  reproducibility gates, and billable measured evaluations remain on demand.
- Plugin registration does not install global instructions or native agents. Windows
  does not support the optional AgentTool bin link and requires symlink capability.
- JEV thresholds are heuristics, not security authorization or exact-fact mechanisms;
  model aliases may move and private responses may remain sensitive.
