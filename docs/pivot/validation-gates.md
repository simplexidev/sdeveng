# Validation gate sets

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

## Blocking final PR-head checks

The final head of every pull request must pass the complete PR-head set:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj
dotnet tools/AgentTool.cs validate --json
dotnet format tests/SdevEng.Tests/SdevEng.Tests.csproj --no-restore --verify-no-changes
git diff --check
```

The hosted CI pull-request run enforces this set. Push-triggered CI remains
advisory and does not establish final PR-head readiness; rerun or inspect the
blocking pull-request checks on the current head before merge.

## Release checks

The tag-triggered release workflow runs this release set before creating a
draft release:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj
dotnet tools/AgentTool.cs validate --json
test "$RELEASE_TAG" = "v$(jq -r .version config/toolkit.json)"
dotnet tools/AgentTool.cs eval --json
dotnet tools/AgentTool.cs release --output artifacts/sdeveng.zip --json
(cd artifacts && sha256sum sdeveng.zip > SHA256SUMS)
```

The workflow then creates a draft GitHub release with the archive and checksum
for human review. These release checks are separate from the per-WorkUnit and
final PR-head sets.
