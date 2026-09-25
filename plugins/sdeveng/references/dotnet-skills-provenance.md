# Official .NET skills provenance

This is compact routing guidance for agents. The machine-readable source of
record is [`upstream/dotnet-skills.json`](../../../upstream/dotnet-skills.json).
It inventories the complete official `dotnet/skills` snapshot inspected on
2026-09-22: 16 plugins, 100 skills, commit
`4ed5f7c121da8dd31af31a35cef05070948c6556`, MIT licensed by the .NET
Foundation and Contributors.

Do not install the upstream marketplace wholesale and do not copy its skill
bodies into this plugin. Select one upstream plugin or one compact local
reference only after a concrete trigger. All upstream paths in the manifest
are relative to the pinned repository commit; static-cost totals include skill
entry files and separately report supporting files.

## Routing decisions

| Area | Disposition | Route |
| --- | --- | --- |
| Test platform detection/execution | `REPLACE_WITH_AGENTTOOL` | Keep project/platform discovery, command construction and result normalization deterministic; retain filter syntax as lazy knowledge. |
| Coverage, CRAP and untested-source facts | `REPLACE_WITH_AGENTTOOL` | Parse formats and calculate metrics in AgentTool; GPT judges behavioral significance. |
| Test quality taxonomies | `CONVERT_TO_REFERENCE` | Load the compact quality checks only for an explicit audit or unclear candidate; do not load six overlapping skills. |
| Test authoring, testability and migrations | `KEEP_UPSTREAM` | Use the compact local workflow first; select exact upstream framework or migration guidance only when repository patterns are insufficient. |
| MSBuild binlogs and performance | `KEEP_UPSTREAM` | Enter through compact local diagnose/optimize skills; prefer prerelease BinlogMcp structured queries on demand. |
| MSBuild authoring patterns | `CONVERT_TO_REFERENCE` | Load the compact diagnostic or performance branch only after evaluated facts select it. |
| Runtime diagnostics and performance | `KEEP_UPSTREAM` | AgentTool chooses collection mechanics; load one upstream analysis branch and never send raw dumps/traces to JEV. |
| File-based C#, P/Invoke, SIMD and trusted publishing | `KEEP_UPSTREAM` | Use only for their narrow triggers and pair with compiler, ABI, benchmark or authorization evidence. |
| SDK setup | `REPLACE_WITH_AGENTTOOL` | Detect SDK/architecture exactly and preserve an explicit boundary before downloads or environment mutation. |
| AOT/API and framework upgrades | `KEEP_UPSTREAM` | Use analyzer/build evidence first; select the exact source/target version guidance. |
| ASP.NET Core, data and templates | `KEEP_UPSTREAM` | Useful domain workflows, separately selected; not core plugin context. |
| Experimental test audits, broad UI/mobile stacks, volatile AI/.NET 11 specialties | `DO_NOT_INSTALL` | Reconsider only for explicit product work or after upstream graduation/current-source verification. |

`VENDOR_UNCHANGED` and `VENDOR_OPTIMIZE` are valid portfolio classifications,
but neither is used: repository policy requires references to upstream .NET
content rather than vendoring it.

## Intelligence boundary

Exact repository, compiler, analyzer, build graph, package, coverage, benchmark,
trace, dump, ABI and symbol facts are deterministic. JEV is limited to bounded,
sanitized candidate ranking where the manifest explicitly permits it; it is not
a source of technical facts. GPT adds value for causal diagnosis, behavior and
test adequacy, migration sequencing, interop ownership/lifetime, and tradeoffs
after deterministic narrowing.

## License and updates

The toolkit currently copies no upstream implementation or substantial
documentation. The pinned repository, commit, paths and MIT attribution are
recorded in the manifest and `THIRD-PARTY-NOTICES.md`. If repository policy ever
allows copying, preserve the upstream MIT license and copyright notice, record
each source path and local modification, and block release until notices match
the redistributed content.

On refresh, inspect the new upstream `main` head, recompute plugin/skill/support
counts, require every upstream `SKILL.md` path to appear in a decision, review
plugin MCP/tool changes, update the pinned SHA, and rerun metadata tests.
