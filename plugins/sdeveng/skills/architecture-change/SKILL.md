---
name: architecture-change
id: architecture-change
version: 3.0.0
description: Plan or review a real change to component boundaries or system architecture.
activation: '[{"id":"csharp-symbol-boundary-change"}]'
resources: '[{"path":"references/symbol-aware-csharp.md","type":"procedure","hash":"sha256:3003168922877c1d619da0b3cdd28e4cea6402c1340f6cd2dd67b804313b0552"}]'
---

# Architecture Change

Map dependency direction and existing extension points first. State compatibility, public API, migration, testing and operational implications. Compare concrete alternatives against project constraints. Keep decisions and implementation scope with Codex; record an ADR only when useful or required.

For a symbol-aware C# boundary change, follow the [symbol-aware C# procedure](references/symbol-aware-csharp.md). Activate this skill only for a requested change to component boundaries or architecture; ordinary method extraction does not qualify.
