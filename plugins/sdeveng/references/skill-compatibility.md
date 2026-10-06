# Skill compatibility inventory (map version 1)

The canonical [map](../../../config/skill-compatibility-map.json) records every
current skill's old and canonical ID/path, plugin version, qualified discovery
alias and migration status. All current entries are retained. Versions track the
owning plugin release, not a claim of independently versioned skill bodies.

| Retained owner | Current consumer / compatibility boundary |
| --- | --- |
| `plugins/sdeveng/plugin.json` | `PluginManifests` owns portable identity and derives `.codex-plugin/plugin.json`; `.agents/plugins/marketplace.json` selects this single plugin. |
| `plugins/sdeveng/skills/*/SKILL.md` | Portable/Codex discovery and `Installer.Plan` use the same directories. Installer links them to `~/.agents/skills/<id>`; it creates no body copies. |
| `skills/*/agents/openai.yaml` | Codex display and activation prompts use the existing unqualified `$<id>`; `Validation.Run` checks metadata. Qualified `<plugin>:<id>` aliases in the map resolve to the same body. |
| `templates/project` | `templates/project/AGENTS.md` and `README.agent.md` consume toolkit guidance; this single template contains no separate skill library. |
| `RuntimeReferences` | Validates lazy local Markdown references from skills and reference files. |
| `Validation`, `PluginManifests` | The existing `validate` command and release preflight now read and validate the map. Discovery and installation remain directory based; aliases do not create installed directories. |
| `config/capabilities.json` | Capability routing inventories skill/command support and activation cost; it is descriptive, not a second discovery registry. |
| `upstream/dotnet-skills.json` | `DotnetSkillsDrift` consumes pinned provenance and routing decisions. No upstream bodies are copied. |

Optimized engineering references remain lazy, model neutral content:
`advanced-dotnet-routing.md`, `msbuild-diagnostics.md`,
`msbuild-performance.md`, `test-framework-edge-cases.md`,
`test-platform-edge-cases.md`, and `test-quality-checks.md`. Their pinned upstream
paths, license, byte/word portfolio measurements and decisions remain in
[provenance](../../../upstream/dotnet-skills.json) and
[routing guidance](dotnet-skills-provenance.md). `capability-coverage.md` and
`v2-baseline.md` retain their current descriptive role. JEV's skill-local
`primitives.md` and `thresholds.md` remain local runtime guidance.

The bounded extension is the map, compatible typed reader and validator;
`extension-required` and `migration-required` are explicit future dispositions,
not instructions to perform a migration. No current skill requires an ID/path
migration. Preserve the derived Codex manifest and installed links. Any future
change of identity must retain the old ID as a resolvable alias and separately
prove path transition safety. No model-family copies are qualified by this
inventory; such copies require measured savings and unchanged safety. Future
runtime/binding consumers are deferred to their own Steps.
