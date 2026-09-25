# MSBuild performance branches

Load only after a comparable binlog or timing baseline selects a branch. Source: official `dotnet/skills` at commit `4ed5f7c121da8dd31af31a35cef05070948c6556` (MIT), `plugins/dotnet-msbuild/skills/{build-perf-baseline,build-perf-diagnostics,build-parallelism,eval-performance,incremental-build,resolve-project-references}/SKILL.md`. This index adds no vendored implementation.

- Evaluation: compare evaluation duration and repeated property/item/glob work before changing target execution.
- Incremental: inspect declared inputs/outputs and skipped-target evidence. Validate both no-op and changed-input builds; never trade correctness for a faster warm build.
- Parallelism: inspect the critical path, graph shape, node utilization, and serialization points. More nodes are not proof of improvement.
- Project references: inspect graph construction and redundant builds across configurations/TFMs before altering reference semantics.
- Measurement: compare the same SDK, machine, configuration, cache state, and workload. Change one variable and retain the baseline.
