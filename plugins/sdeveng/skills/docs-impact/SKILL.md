---
name: docs-impact
description: Assess documentation changes required by a code, configuration or public API change.
---

# Docs Impact

Start with `repo changed-files` and identify changed user-visible behavior, configuration keys and public APIs. Search doc references for those names. If deterministic matches suffice, skip JEV. For a large ambiguous set, use capability `relevance` with purpose `docs-impact`, screen only sanitized titles/excerpts, and open INCLUDE plus REVIEW. Update examples and migration notes that are now inaccurate; avoid unrelated prose cleanup.
