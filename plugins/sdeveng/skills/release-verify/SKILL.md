---
name: release-verify
description: Run explicitly requested release or complete .NET validation.
---

# Release Verify

Use the project's configured release checks; for .NET, `dotnet release-verify --project PATH` covers restore, build, formatting, detected tests and audit. Inspect the produced archive with `artifact inspect --file PATH`, then use `artifact verify --file PATH --sha256 HEX` against a trusted checksum. Add configured API baselines, reproducibility and SBOM checks separately—the workflow does not certify absent gates. Preserve reports and list every executed, skipped or blocked gate.
