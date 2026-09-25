---
name: prepare-commit
description: Check an intended local commit's scope and Git safety.
---

# Prepare Commit

Run `sdeveng git prepare-commit --json`; examine staged and unstaged diffs, whitespace results and changed file scope. Confirm relevant validation. Stage explicit paths only and preserve unrelated user work. Commit only when requested or covered by the task. Do not bypass unfinished Git operation checks.
