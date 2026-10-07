---
name: git-branch-commit
id: git-branch-commit
version: 3.0.0
description: Create an explicitly requested local branch or commit through guarded Git operations.
supportedRoles: '["coder","repair"]'
contextAllowance: 2200
resources: '[{"path":"workflow.md","type":"procedure","hash":"sha256:1ba734197edd55ee3ba816e0a9cec88f33498e53aedb53c9ec1b2033c26bbd2e"}]'
---

# Git Branch and Commit

Use for an explicit request to create a local task branch or commit. Do not activate for inspection, issue startup, or pull request completion; those use `prepare-commit`, `issue-start`, and `finish-pr` respectively.

Read `workflow.md` when a branch or commit is requested. First inspect `sdeveng git state --json` and the exact owned diff. Stop if a Git operation is unfinished or the intended paths are unclear. Preserve unrelated staged, unstaged, and untracked work.

For a branch, require an explicit branch name and use the guarded branch operation documented in the resource. For a commit, identify explicit owned paths, write them as a JSON string array, stage with `sdeveng git stage-owned --paths-file FILE`, then commit with `sdeveng git commit-owned --paths-file FILE --message TEXT` only when the user requested the commit. Never use raw shell Git mutation, implicit all-path selection, stash/reset/clean, force, merge, auto-merge, or self-approval. Keep local checks, advisory results, and final CI as separate evidence; end at human handoff.
