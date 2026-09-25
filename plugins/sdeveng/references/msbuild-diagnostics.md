# MSBuild diagnostic branches

Load only the branch selected by exact evidence. Source: official `dotnet/skills` at commit `4ed5f7c121da8dd31af31a35cef05070948c6556` (MIT), especially `plugins/dotnet-msbuild/skills/binlog-failure-analysis/SKILL.md`, `resolve-project-references/SKILL.md`, `including-generated-files/SKILL.md`, and `target-authoring/SKILL.md`. This is a compact index, not copied upstream content.

- Evaluation: query evaluated properties/items/import order. Use preprocessing only for the implicated project/configuration; XML alone is not the evaluated result.
- Compiler: start at the first causal diagnostic and its generated/compiled inputs. Later failures may be consequences.
- Project references: inspect the evaluated edge, selected target framework/configuration, and `BuildReference` behavior before editing references.
- Targets/tasks: inspect dependency order, conditions, inputs/outputs, and first failing task. Do not treat target declaration order as execution order.
- Binlogs: keep binaries local. Prefer structured BinlogMcp queries; use bounded text excerpts only if structured analysis is unavailable, and state that limitation.
