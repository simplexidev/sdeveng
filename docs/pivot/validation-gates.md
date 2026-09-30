# WorkUnit validation gates

A WorkUnit is one focused implementation commit. Before pushing it, run this
synchronous local gate from the repository root and wait for every command to
finish successfully:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj
dotnet tools/AgentTool.cs validate --json
dotnet format tests/SdevEng.Tests/SdevEng.Tests.csproj --no-restore --verify-no-changes
git diff --check
```

The test command compiles the product and test project and runs the automated
tests. Validation checks repository-owned contracts and configuration. Format
and diff checks reject whitespace or formatting defects. A failed command blocks
the push until the WorkUnit is repaired and the complete gate passes again.

## Hosted advisory checks

The `CI` workflow may run the same test, validation, format, and diff checks on
each pushed branch. Push-triggered results are advisory and do not block the
push or change local gate results. Pull request runs remain blocking. The
workflow's Ubuntu and Windows test matrix provides hosted platform coverage;
it is supporting evidence and does not replace the synchronous local gate.

CodeQL, release verification, and skill evaluation are separate hosted
workflows. They are not part of the per-WorkUnit local gate.
