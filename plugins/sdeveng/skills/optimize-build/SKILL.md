---
name: optimize-build
description: Optimize a measured slow .NET build, evaluation, project graph, incremental build, or parallel execution.
---

# Optimize Build

Require a reproducible workload and baseline. Use `dotnet inspect` and `dotnet build-plan --binlog`; compare compatible clean or incremental runs without changing multiple variables. Query binlogs structurally for evaluation, target/task duration, critical path, skipped targets, and project-reference scheduling before using raw logs.

Load only the measured branch in [MSBuild performance branches](../../references/msbuild-performance.md). Preserve correctness and incremental inputs/outputs; validate the same workload after one change. JEV does not judge timings, build graphs, or significance.
