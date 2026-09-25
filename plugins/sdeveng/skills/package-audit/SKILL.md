---
name: package-audit
description: Audit .NET package vulnerability metadata on explicit request.
---

# Package Audit

Run `sdeveng dotnet package-audit --project PATH --json`; it requests vulnerable transitive packages in structured JSON and returns nonzero for vulnerabilities. Restore/network failures are failures, not clean audits. Report package, resolved version, advisory and affected project from the full artifact. Never substitute manual review of a huge package list or JEV for advisory metadata.
