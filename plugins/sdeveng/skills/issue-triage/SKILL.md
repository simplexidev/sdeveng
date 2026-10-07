---
name: issue-triage
description: Classify and route a specific GitHub issue using its facts, labels, and repository context.
---

# Issue Triage

Use this only when asked to classify, prioritize, or route a specific GitHub issue. Identify the explicit issue number and canonical repository first; do not search for or select issues implicitly. Read that issue and the configured label catalog, then gather only relevant repository facts referenced by the issue. Separate observed facts from assumptions and explain uncertainty.

Recommend applicable configured area, risk, and complexity labels from the evidence. Preserve all existing labels and never invent labels, issue scope, owners, or priority facts. If the issue is already in progress, the evidence is ambiguous, or no configured label applies, stop with a concise explanation and hand off to a human.

Label writes require explicit user authorization and the guarded issue-label capability for the same canonical repository. Use the typed issue triage operation when available; never call raw GitHub mutation APIs or shell commands to change labels. Do not start a branch, assign work, close an issue, create a pull request, merge, enable auto-merge, or approve anything as part of triage. Finish with the evidence, recommendation, any authorized change, and the human handoff needed.
