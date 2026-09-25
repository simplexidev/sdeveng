# SimplexiDev Engineering Toolkit (`sdeveng`)

The runtime product for SimplexiDev Engineering Toolkit: one unified plugin, one project template,
AgentTool, runtime skills and references, and one evidence-backed native agent. The
toolkit prefers deterministic tooling, then bounded JEV judgment, then Codex reasoning.

Human documentation is owned by `simplexidev/sdeveng-docs`. Until the repository-rename
phase, it is served from the legacy
[`simplexidev/codex-toolkit-docs`](https://github.com/simplexidev/codex-toolkit-docs)
location.
Start with its [installation guide](https://github.com/simplexidev/codex-toolkit-docs/blob/main/docs/getting-started/install.md),
then see [usage and configuration](https://github.com/simplexidev/codex-toolkit-docs/tree/main/docs/guides),
the [reference](https://github.com/simplexidev/codex-toolkit-docs/tree/main/docs/reference),
and [troubleshooting](https://github.com/simplexidev/codex-toolkit-docs/tree/main/docs/troubleshooting).

## Quick start

Requires the .NET 10 SDK and Git. From this checkout:

```text
dotnet tools/AgentTool.cs install --dry-run
dotnet tools/AgentTool.cs install --bin
dotnet tools/AgentTool.cs doctor
```

AgentTool installs links without overwriting user configuration. It never commits,
pushes, merges, or creates remote repositories. Run `dotnet tools/AgentTool.cs help`
for the bounded command surface.

JEV is optional. Its compact, agent-consumed operating reference remains product-local
at [docs/jev.md](docs/jev.md); broader explanation belongs in the human
[JEV documentation](https://github.com/simplexidev/codex-toolkit-docs/blob/main/docs/concepts/jev.md).
Runtime skills never depend on the documentation repository.

## Repository map

- `tools/AgentTool.cs` — the .NET 10 file-based runtime utility.
- `plugins/sdeveng/` — the unified plugin, skills, and runtime references.
- `templates/project/` — the project integration template.
- `agents/` and `global/` — native-agent and installed instruction definitions.
- `config/` and `schemas/` — runtime policy and validated configuration.
- `tests/` and `evals/` — automated checks and smoke evaluation inputs.

For architecture, security, examples, development, and contributor guidance, use the
human documentation repository (currently served from the legacy
[`codex-toolkit-docs`](https://github.com/simplexidev/codex-toolkit-docs) location).
Release history stays canonical in [CHANGELOG.md](CHANGELOG.md), and vulnerability
reporting guidance remains in [SECURITY.md](SECURITY.md).

## Validate

```text
dotnet test tests/AgentTool.Tests/AgentTool.Tests.csproj
dotnet tools/AgentTool.cs validate
dotnet tools/AgentTool.cs eval
```

Tests use temporary homes and fake JEV HTTP responses. Normal validation makes no live,
billable JEV call.
