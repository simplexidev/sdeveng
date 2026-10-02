# Validation gate sets

A product Run may record validation evidence for an exact commit. The current
runtime does not create WorkUnits. A focused local change should pass:

```sh
dotnet build SdevEng.slnx --configuration Release
dotnet test SdevEng.slnx --configuration Release
dotnet tools/AgentTool.cs validate --json
dotnet format SdevEng.slnx --no-restore --verify-no-changes
git diff --check
```

## Hosted advisory checks

Each pushed commit gets its own Ubuntu and Windows CI run. The workflow selects
affected projects from the evaluated project graph, builds them in Release mode,
runs affected tests with coverage, validates product contracts and formatting,
and uploads exact-commit evidence. Intermediate push failures are advisory.

## Blocking final PR-head checks

The same matrix checks the exact pull-request head and additionally validates a
source package containing the solution and production projects. Pull-request
failures block progression. Evidence records actual observed checks and lists
skipped checks separately; no skipped check is presented as passed.
The workflow then applies `config/verification-evidence-policy.json` to that
normalized exact-head evidence. Missing or failed required checks reject the PR
gate. An optional local bundle can be compared with the same commit and check
identities through `verification decide`; disagreements appear in the decision.

## Release checks

A tag-triggered Ubuntu and Windows matrix builds the full solution in Release
mode, runs every test project with coverage, validates contracts and formatting,
checks the source package, and compares repeat Release build output. Both
platform observations must match the repository, commit, and tag and pass before
the workflow publishes the release-validation manifest. Only then does it
create a draft release for human review.
