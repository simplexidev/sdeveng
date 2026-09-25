---
name: dotnet-coverage
description: Collect or interpret targeted .NET coverage evidence without treating percentage alone as test quality.
---

# .NET Coverage

Use this skill only for supplied coverage evidence or an explicit coverage
request. If a Cobertura or OpenCover file exists, summarize it first with
AgentTool `coverage summarize`; do not rerun tests, install tools, or generate a
report merely to restate available facts.

For collection, start with the smallest requested or affected test project from
AgentTool `dotnet test-plan`. Prefer the repository's existing coverage command
and provider. If platform-specific collection syntax is not established, read
[`test-platform-edge-cases.md`](../../references/test-platform-edge-cases.md)
and stop rather than adding/upgrading packages without authorization. Widen
collection only when project boundaries or a stated threshold require it.

Treat counters as exact measurements: reconcile covered and valid totals and
identify the denominator. A covered line proves execution, not both branch
outcomes or a discriminating assertion. Relate uncovered lines/branches to
observable behavior before proposing tests. Coverage-backed risk ranking may
combine exact counters with code reasoning, but mutation/CRAP analysis is
separate and opt-in.

JEV capability `relevance` with purpose `coverage-gap-ranking` may rank a large
sanitized list of already-computed candidate gaps. It must
not parse reports, calculate percentages, set thresholds, or decide whether a
behavior is adequately tested. Keep raw reports local and pass only bounded
summaries to models.
