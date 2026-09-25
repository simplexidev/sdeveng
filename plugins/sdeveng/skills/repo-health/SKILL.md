---
name: repo-health
description: Audit repository configuration and evidence-backed hygiene candidates.
---

# Repo Health

Run `repo health` for evaluated .NET policy and `repo hygiene` for tracked generated/output/temporary candidates. Inspect the evidence for each candidate before recommending cleanup; the report does not establish that a generated file is unused and never deletes it. Read config/repo-health.json for configured build policy. Report actual versus expected values and propose targeted changes without imposing optional release tooling or weakening policy to obtain a pass.
