---
name: security-scan
description: Run or triage deterministic security analyzers and SARIF for a scoped change.
---

# Security Scan

Use configured CodeQL, DevSkim, language analyzers and dependency audits as applicable. Prefer `sarif summarize --file PATH`; when a trusted prior scan exists, pass `--baseline PATH` and investigate new findings first without hiding unchanged or fixed counts. Preserve severity, rule IDs, locations and suppression context. For a large sanitized finding set, JEV capability `sarif-triage` with purpose `finding-relevance-categorization` may group review order; it never changes severity, suppresses, downgrades, or authorizes action. Avoid external transmission of sensitive source; validate fixes with the originating rule.
