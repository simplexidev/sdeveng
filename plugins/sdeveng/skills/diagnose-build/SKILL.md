---
name: diagnose-build
description: Diagnose a concrete .NET restore, compiler, MSBuild evaluation, target, or project-reference failure.
---

# Diagnose Build

Run `sdeveng dotnet inspect --json` for environment/project facts, then `sdeveng dotnet build-plan --binlog --json` for the affected or explicit target. Check the selected SDK against `global.json` and the installed SDK list, and use the evaluated target frameworks and project graph from inspection; do not infer evaluated values from project XML. If `dotnet` or a required SDK is unavailable, report the toolchain as unavailable/unsupported and stop before executing the plan. Execute only the needed plan. Keep restore and build outcomes distinct: a planned or skipped command is not a passing build, and report the actual failing command and exit result. Prefer the first causal compiler diagnostic; when console evidence is insufficient, query the binlog structurally with the pinned upstream BinlogMcp workflow before reading bounded raw-log excerpts.

Use [MSBuild diagnostic branches](../../references/msbuild-diagnostics.md) only after evidence identifies evaluation, compilation, project references, or target execution. Do not infer evaluated properties from XML and do not send a binlog to JEV or a model.
