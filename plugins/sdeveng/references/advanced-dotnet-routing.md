# Runtime and advanced .NET routing

Use exact evidence before selecting one upstream workflow. The source inventory is `upstream/dotnet-skills.json`, pinned to official `dotnet/skills` commit `4ed5f7c121da8dd31af31a35cef05070948c6556` (MIT). The relevant sources begin under `plugins/dotnet-diag/skills/`, `plugins/dotnet-advanced/skills/`, `plugins/dotnet/skills/`, and `plugins/dotnet-upgrade/skills/`. No upstream implementation is vendored.

| Trigger | Route | Required evidence |
| --- | --- | --- |
| CPU, contention, allocation, GC, dump, activation, or symbolication | Keep upstream `dotnet-diag`; AgentTool plans collection | OS/runtime/SDK, PID or artifact, symptom and bounded window |
| File-based app | Keep upstream `dotnet-advanced/csharp-scripts` | Explicit project-free `.cs` app and current SDK/compiler result |
| Advanced C# refactor or language behavior | Keep upstream `dotnet/csharp-refactoring` or compiler/vendor docs | Actual symbols, diagnostics, consumers and tests |
| Public/package API compatibility, trimming, or AOT | AgentTool/analyzers first; keep upstream specialist guidance | Baseline, analyzer/package-validation or publish diagnostics |
| P/Invoke/ABI/marshalling | Keep upstream `dotnet-advanced/dotnet-pinvoke` | Native header/export, OS, architecture, ABI, ownership/lifetime |
| SIMD/vectorization | Keep upstream only after profiling | Measured hot numeric loop, ISA and representative benchmark |

JEV may rank a large sanitized candidate list only after deterministic extraction. It never supplies compiler, package, ABI, symbol, trace, dump, or performance facts. Vendor none of these workflows; fast-moving syntax and high-consequence platform guidance stay with the official source.
