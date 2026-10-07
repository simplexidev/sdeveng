# Guarded local branch and commit operations

The typed Git command surface is:

- `sdeveng git branch-create --branch NAME --json` creates only the explicitly named local branch and checks repository preconditions.
- `sdeveng git state --json` reports branch, worktree, and unfinished operation facts before and after a mutation.
- `sdeveng git prepare-commit --json` reports diff scope and whitespace checks without staging or committing.
- `sdeveng git stage-owned --paths-file FILE` stages only the explicit JSON path list and rejects unrelated workspace changes.
- `sdeveng git commit-owned --paths-file FILE --message TEXT` commits only when the staged path set exactly matches the explicit list.

Use a temporary paths file containing a JSON array of repository-relative strings. Review the staged diff and local validation before committing. A failed precondition means stop and report the observed state. Do not retry by broadening paths or using raw `git` mutation. This workflow ends with local branch or commit evidence; push, PR, and merge decisions belong to separately authorized workflows and a human handoff.
