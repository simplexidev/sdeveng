---
name: finish-pr
description: Prepare an authorized push and pull request after implementation is ready.
---

# Finish Pr

Use `sdeveng github prepare-pr --json` and inspect the exact diff, checks, branch and upstream. Before a likely base update or merge, run `sdeveng git conflict-forecast --base REF --json`; it does not change refs, index or worktree, and its result is a forecast rather than permission to merge. For authorized staging, write only the intended paths as a JSON string array in a paths file and run `sdeveng git stage-owned --paths-file FILE`; it rejects unrelated workspace changes. Commit only when authorized with `sdeveng git commit-owned --paths-file FILE --message TEXT`, then push only the named branch to the intended remote with `sdeveng git push-owned --remote NAME --branch NAME`. On a failed push, use the returned remote, branch, and head to inspect and retry. Open/update a PR with a concrete summary and validation using available GitHub tools or gh for authorized external actions. Never merge without explicit user approval or use destructive recovery for an unfinished Git operation.
