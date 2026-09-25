---
name: issue-start
description: Start work on a specific open GitHub issue with a safe task branch.
---

# Issue Start

Read the issue and check `sdeveng git state --json`. Use `sdeveng git issue-start --issue NUMBER --branch NAME --json`; it requires an open issue, clean attached branch and no unfinished operation. Branch names reflect the user's issue, not arbitrary remote guesses. Preserve dirty work; do not stash or reset to force readiness.
