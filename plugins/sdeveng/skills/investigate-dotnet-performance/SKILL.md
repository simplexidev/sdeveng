---
name: investigate-dotnet-performance
description: Investigate an observed .NET latency, throughput, CPU, allocation, or memory regression.
---

# Investigate .NET Performance

Record the workload, symptom, baseline, environment, and measurement window. Reuse comparable counters, traces, profiles, or benchmark results first. Run `dotnet diagnostics-plan` only for the narrow missing signal; it owns collection mechanics and keeps binary artifacts under `.agent-tool/traces`.

Separate CPU, contention, I/O, GC, allocation, and retention hypotheses using measured evidence. Follow one causal branch to code, then confirm under the same workload. Create a microbenchmark only when it represents the observed path. Never send raw traces/dumps to JEV or a model; use bounded structured summaries. Load [runtime and advanced .NET routing](../../references/advanced-dotnet-routing.md) only when the evidence selects a specialist upstream workflow.
