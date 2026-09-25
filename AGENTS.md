# Toolkit development

All production executable logic belongs in `tools/AgentTool.cs`, the single-file .NET 10
implementation behind the public `sdeveng` command and legacy `codex-agent-tool` alias.
Keep commands composable and output bounded. Use BCL APIs unless a dependency has a
written correctness/interoperability justification. Tests may use established test and parser libraries.
Do not introduce helper scripts in other languages. Skills must have narrow triggers,
progressive disclosure, and observable regression cases. Reference upstream .NET content;
do not vendor it. Keep schemas and documentation synchronized with behavior changes.
Meaningful behavior changes require automated tests and relevant evaluation cases.

Installer tests must use temporary homes, never the developer's actual Codex configuration.
JEV tests use fake HTTP responses only; never make a live billable call during validation.

Use durable results only when a later independent chat needs non-obvious state; reference large artifacts by path. Git history is enough for trivial work, and normal repository discovery must not search historical results.
