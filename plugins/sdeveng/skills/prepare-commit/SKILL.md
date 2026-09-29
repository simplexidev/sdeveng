---
name: prepare-commit
description: Check an intended local commit's scope and Git safety.
---

# Prepare Commit

Run `sdeveng git prepare-commit --json`; examine staged and unstaged diffs, whitespace results and changed file scope. Confirm relevant validation. When staging is authorized, write the exact owned paths as a JSON string array in a paths file, then run `sdeveng git stage-owned --paths-file FILE`. It rejects unrelated workspace changes; preserve those changes before retrying. Commit only when requested or covered by the task, using `sdeveng git commit-owned --paths-file FILE --message TEXT`; the staged path set must exactly match that file. Preserve unrelated user work and do not bypass unfinished Git operation checks.
