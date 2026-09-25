---
name: versioning
description: Apply an existing Git-derived .NET versioning policy for a release or package change.
---

# Versioning

Inspect version.json and Git history/tag state. Prefer configured Nerdbank.GitVersioning metadata when present; use its output rather than inventing versions. Discuss compatibility signals and prerelease policy. Modify version policy only within the request. Missing version tooling is reported, not replaced by guessed package numbers.
