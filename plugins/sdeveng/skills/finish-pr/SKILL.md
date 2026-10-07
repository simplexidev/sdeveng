---
name: finish-pr
id: finish-pr
version: 3.0.0
description: Prepare a specific completed change for human pull request handoff using guarded Git procedures.
supportedRoles: '["coder","reviewer"]'
---

# Finish Pr

Use this procedure only when the user asks to prepare or hand off a specific completed change.

1. Run `sdeveng git state --json` and `sdeveng git prepare-commit --json`. Confirm the intended branch, upstream, exact changed paths, staged and unstaged diffs, whitespace result, and absence of an unfinished Git operation. If an operation is unfinished, stop and report its exact state; do not recover it.
2. Run `sdeveng github prepare-pr --json` for the current branch. Review its local diff and validation facts. Label evidence accurately: local checks describe this checkout, conflict forecast is advisory only, and final CI is the hosted result for the pushed commit. Do not present one as another.
3. If a base update is being considered, run `sdeveng git conflict-forecast --base REF --json`. Treat it as a read-only forecast, never as authorization or a prediction of hosted CI.
4. Stage only user-authorized, explicitly named paths. Put exactly those paths in a JSON string array and use `sdeveng git stage-owned --paths-file FILE`. If it rejects scope or detects unrelated changes, stop and report the result; preserve unrelated work.
5. Create a local commit only when authorized, using the same exact paths file with `sdeveng git commit-owned --paths-file FILE --message TEXT`. Push only when authorized, naming the intended remote and branch explicitly with `sdeveng git push-owned --remote NAME --branch NAME`. Check the returned target and head. On failure, inspect that result before any retry; never broaden the target.
6. End with a human handoff containing the branch/head, concise change summary, local validation, advisory forecast if used, and final hosted CI status only when observed. The available guarded commands do not create PRs: the user may create or update one through their approved workflow. Do not use raw Git or `gh` write commands, merge or enable auto-merge, approve your own PR, or imply a PR was updated when it was not.
