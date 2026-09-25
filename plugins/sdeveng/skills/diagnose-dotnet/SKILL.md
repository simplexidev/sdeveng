---
name: diagnose-dotnet
description: Diagnose a reported .NET runtime crash, hang, contention, allocation, GC, or memory issue.
---

# Diagnose .NET

Start with the reported symptom and any caller-provided artifact. Run `sdeveng dotnet diagnostics-plan --json` with the matching `--signal`, PID when known, and the shortest useful duration. It determines OS, runtime, SDK, installed tools, artifact path, and a non-executing collection command. Inspect an existing trace, dump, or GC dump before collecting again.

Keep large artifacts local and outside model/JEV context. Use structured tool output and bounded, sanitized summaries. Follow only the selected branch in [runtime and advanced .NET routing](../../references/advanced-dotnet-routing.md); collection requires user authority when it affects a live process.
