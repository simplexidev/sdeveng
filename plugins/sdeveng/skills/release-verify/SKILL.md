---
name: release-verify
description: Run explicitly requested release or complete .NET validation.
---

# Release Verify

Use the project's configured release checks; for .NET, `sdeveng dotnet release-verify --project PATH --json` covers restore, build, formatting, detected tests and audit. Inspect the produced archive with `sdeveng artifact inspect --file PATH --json`, then use `sdeveng artifact verify --file PATH --sha256 HEX --json` against a trusted checksum whose source and owner are recorded. For a durable gate record, run `sdeveng release evidence --profile NAME --output REPOSITORY_RELATIVE_PATH --json`, supplying trusted evidence with `--evidence-file PATH` and an expected tag with `--tag TAG` when applicable. The manifest binds gates and artifact hashes to a repository and commit; review its provenance and confirm each artifact belongs to that release before relying on it. Add configured API baselines, reproducibility and SBOM checks separately—the workflow does not certify absent gates. Preserve reports and list every executed, skipped or blocked gate; unsupported and not-observed gates are not passes, and missing required gates prevent a readiness claim.
