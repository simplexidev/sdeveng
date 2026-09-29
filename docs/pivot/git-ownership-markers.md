# Git ownership markers

An owned branch carries the local Git configuration entry `branch.<name>.sdeveng-owned=true`. The branch name is the exact local branch name. A missing entry or any value other than `true` does not mark ownership.

An owned linked worktree carries a file named `sdeveng-owned-worktree` in its private Git directory, as reported by `git rev-parse --absolute-git-dir` from that worktree. Its exact UTF-8 contents are `sdeveng-owned-worktree-v1` followed by one LF. The marker is outside the checkout, so it does not change the user's tracked or untracked files. A missing file or different contents does not mark ownership.

These definitions do not mark existing branches or worktrees as owned. Creation and enforcement belong to later steps.
