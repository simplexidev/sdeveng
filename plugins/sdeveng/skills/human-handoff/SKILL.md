---
name: human-handoff
id: human-handoff
version: 3.0.0
description: Summarize bounded Git and GitHub evidence and stop for a human decision.
supportedRoles: '["planner","coder","reviewer","repair"]'
requiredTools: '["verification_decide"]'
resources: '[{"path":"references/handoff-evidence.md","type":"reference","hash":"sha256:2b691cb23458de63c265f51d4f07c68af22dcaeae0189c36af02fb43fb895fe6"}]'
---

# Human Handoff

Use after a specific Git or GitHub task reaches its authorized stopping point. Select only the explicit branch, commit, pull request, or run named by the user or the current task. Gather facts with the guarded typed tools and preserve unrelated changes.

1. Report local validation as results for this checkout and revision. Report a conflict forecast separately as read-only advisory evidence. Report final CI only from hosted results for the exact pushed commit. Mark each category unavailable when its evidence was not observed.
2. Summarize the selected target, current state, completed work, validation evidence, unresolved issues, and the decision or authorization needed from a human.
3. Stop at that human handoff. Do not merge, enable auto-merge, approve, push, create or update a pull request, or use raw shell/`gh` mutation. Never infer a human decision or claim that a remote change occurred.

Use the [handoff evidence reference](references/handoff-evidence.md) when deciding how to label each evidence source.
