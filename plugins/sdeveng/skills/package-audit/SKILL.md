---
name: package-audit
description: Audit .NET package vulnerability metadata on explicit request.
---

# Package Audit

Use `sdeveng dotnet package-audit --project PATH --json` for an explicit audit. Select the affected project or solution from the change; do not broaden to unrelated projects. Check the installed SDK with `dotnet --version` and the target framework in the project before interpreting results. The command uses `dotnet package list --vulnerable --include-transitive --format json`; if the SDK does not support those options, report the check as unsupported and do not claim a clean audit.

The command returns nonzero when vulnerabilities are found. Restore, network, tool, or malformed-output failures are failures, not clean audits. Use the full artifact to report affected project, package, resolved version, advisory and severity, including transitive findings. A successful empty vulnerability result means no findings from this audit. Do not replace advisory metadata with a manual review of a large package list or JEV.
