---
name: finish-pr
description: Prepare an authorized push and pull request after implementation is ready.
---

# Finish Pr

Use `sdeveng github prepare-pr --json` and inspect the exact diff, checks, branch and upstream. Before a likely base update or merge, run `sdeveng git conflict-forecast --base REF --json`; it does not change refs, index or worktree, and its result is a forecast rather than permission to merge. Stage only intended files, commit and push only within the user's scope, then open/update a PR with a concrete summary and validation. Use available GitHub tools or gh for authorized external actions. Never merge without explicit user approval or use destructive recovery for an unfinished Git operation.
