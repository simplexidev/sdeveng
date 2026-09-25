# `sdeveng` CLI contract

`sdeveng` is the canonical deterministic runtime command. `sdeveng version` and
`sdeveng --version` report the installed CLI version. A checkout remains directly
invokable as `dotnet tools/AgentTool.cs`; installations made with `--bin` also retain
the deprecated `codex-agent-tool` alias for v2 automation while users migrate.

Pass `--json` whenever output is consumed by a skill, script, or other program. The
command writes one JSON object conforming to `schemas/cli-result.schema.json`. Its
top-level `schemaVersion` versions the envelope; `command`, `status`, `exitCode`,
`data`, and `error` must be read by name. Command-specific `data.kind` contracts and
their versions are listed in `config/agent-tool-contracts.json`. Human/default output
is not a parsing contract.

Exit codes are stable: `0` means success, `1` means the command completed and found a
negative outcome such as findings or a failed check, `2` means invalid invocation or
invalid input, `3` means a configured required dependency was unavailable, and `70`
means an unexpected internal failure. Structured invocation/internal errors are written
to standard error; command results are written to standard output.

Result envelope v1 may gain new command-specific fields under `data`, but existing
top-level fields do not change meaning. An incompatible envelope change requires a new
`schemaVersion`; incompatible command-data changes require that command contract's
`schemaVersion` to increase.
