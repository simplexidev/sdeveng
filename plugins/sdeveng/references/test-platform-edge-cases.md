# .NET test platform edge cases

Load this reference only after `dotnet test-plan` cannot emit an exact command
or a platform-specific option is explicitly needed.

- SDK 10 native Microsoft.Testing.Platform mode is selected by
  `global.json` `test.runner`; it uses `dotnet test --project <path>` and direct
  MTP arguments.
- A VSTest-command-mode project can bridge to an executable MTP application
  only when the evaluated project enables `TestingPlatformDotnetTestSupport`
  and produces an executable. Put MTP arguments after `--`.
- VSTest uses positional project paths and `--filter`. MTP with MSTest/NUnit can
  use the same expression, xUnit v3 uses its framework filter flags/query, and
  TUnit uses a tree-node filter. Prefer AgentTool's structured `--test`,
  `--class`, or `--category` inputs over translating a raw expression.
- Classic non-SDK projects keep their repository script or full-MSBuild runner.
  Do not silently migrate them or assume `dotnet test`.
- Coverage, crash/hang diagnostics, watch mode, and custom loggers depend on
  installed extensions. Inspect evaluated packages and repository commands
  before adding a flag or dependency.

Provenance: original compact synthesis from `dotnet/skills` commit
`4ed5f7c121da8dd31af31a35cef05070948c6556`, MIT, copyright .NET Foundation
and Contributors; source paths
`plugins/dotnet-test/skills/platform-detection/SKILL.md`,
`plugins/dotnet-test/skills/run-tests/SKILL.md`, and
`plugins/dotnet-test/skills/filter-syntax/SKILL.md`. No upstream text or code is
redistributed verbatim.
