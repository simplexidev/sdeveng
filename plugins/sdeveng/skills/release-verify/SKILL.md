---
name: release-verify
description: Run explicitly requested release or complete .NET validation.
---

# Release Verify

Use the project's configured release checks; for .NET, `sdeveng dotnet release-verify --project PATH --json` covers restore, build, formatting, detected tests and audit. Inspect the produced archive with `sdeveng artifact inspect --file PATH --json`, then use `sdeveng artifact verify --file PATH --sha256 HEX --json` against a trusted checksum. Add configured API baselines, reproducibility and SBOM checks separately—the workflow does not certify absent gates. Preserve reports and list every executed, skipped or blocked gate.
