---
name: roadmap-next
description: Identify an actionable next issue from explicit roadmap and dependency metadata.
---

# Roadmap Next

Read bounded issue metadata and dependency state. First use explicit issue references and repository topology to identify the owning project; select issues owned by the invocation repository, following only explicit cross-repository references. Then narrow candidates to direct dependency neighbors of the selected work and respect declared ordering, milestones and blockers. Do not traverse transitive dependencies or infer ownership from issue prose. Use JEV Choice capability `ambiguous-routing` with purpose `issue-prose-category` only when those deterministic fields leave ambiguous issue prose; unresolved cases stay with GPT. Do not infer strategic priorities from semantic scores. Explain the selected item using roadmap, ownership, and direct dependency evidence.
