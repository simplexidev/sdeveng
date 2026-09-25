# SimplexiDev Engineering Toolkit (`sdeveng`)

The runtime product for SimplexiDev Engineering Toolkit: one unified plugin, one project template,
the `sdeveng` deterministic runtime, skills and references, and one evidence-backed native agent. The
toolkit prefers deterministic tooling, then bounded JEV judgment, then Codex reasoning.

Human documentation is owned by `simplexidev/sdeveng-docs`.
Start with its [installation guide](https://github.com/simplexidev/sdeveng-docs/blob/main/docs/getting-started/install.md),
then see [usage and configuration](https://github.com/simplexidev/sdeveng-docs/tree/main/docs/guides),
the [reference](https://github.com/simplexidev/sdeveng-docs/tree/main/docs/reference),
and [troubleshooting](https://github.com/simplexidev/sdeveng-docs/tree/main/docs/troubleshooting).

## Quick start

Requires the .NET 10 SDK and Git. From this checkout:

```text
dotnet tools/AgentTool.cs install --dry-run
dotnet tools/AgentTool.cs install --bin
sdeveng doctor
sdeveng version
```

The installer links the canonical `sdeveng` command without overwriting user
configuration and retains `codex-agent-tool` as a v2 migration alias. The runtime
never commits, pushes, merges, or creates remote repositories. Run `sdeveng help`
for the bounded command surface and pass `--json` for the documented
[versioned result contract](docs/cli-contract.md).

JEV is optional. Its compact, agent-consumed operating reference remains product-local
at [docs/jev.md](docs/jev.md); broader explanation belongs in the human
[JEV documentation](https://github.com/simplexidev/sdeveng-docs/blob/main/docs/concepts/jev.md).
Runtime skills never depend on the documentation repository.

## Repository map

- `tools/AgentTool.cs` — the single-file .NET 10 implementation behind `sdeveng`.
- `plugins/sdeveng/` — the unified plugin, skills, and runtime references.
- `templates/project/` — the project integration template.
- `agents/` and `global/` — native-agent and installed instruction definitions.
- `config/` and `schemas/` — runtime policy and validated configuration.
- `tests/` and `evals/` — automated checks and smoke evaluation inputs.

For architecture, security, examples, development, and contributor guidance, use the
[`sdeveng-docs`](https://github.com/simplexidev/sdeveng-docs) repository.
Release history stays canonical in [CHANGELOG.md](CHANGELOG.md), and vulnerability
reporting guidance remains in [SECURITY.md](SECURITY.md).

## Validate

```text
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj
sdeveng validate --json
sdeveng eval --json
```

Tests use temporary homes and fake JEV HTTP responses. Normal validation makes no live,
billable JEV call.
