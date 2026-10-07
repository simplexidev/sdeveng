---
name: ci-triage
id: ci-triage
version: 3.0.0
description: Diagnose a failed GitHub Actions run or job from bounded workflow evidence.
supportedRoles: '["planner","coder","reviewer","repair"]'
requiredTools: '["verification_decide"]'
---

# CI Triage

Use the guarded `github-actions-summary` tool (or its `sdeveng github actions --json` command) to list recent runs, then select one explicit run ID and fetch its normalized jobs and failed steps. Fetch failed logs only for that run; start with the earliest causal failure rather than downstream cancellations or repeated errors. Keep evidence bounded to that run, its failed step, and the relevant revision/configuration facts.

Label evidence by source and target: local checks describe the current checkout; advisory forecasts are read-only estimates and do not establish hosted status; final CI is the hosted result for the exact pushed commit. Do not infer final CI from local checks, an advisory result, a different commit, or a partial run. If the final check state is available, use the guarded `verification-progression-decision` tool with hosted evidence and the exact commit, and report its decision separately from the failure diagnosis. If evidence is missing or incomplete, say so and stop short of a final-check claim.

Correlate the failing command with the exact revision and relevant local configuration before proposing a fix. If deterministic evidence leaves at most five sanitized bounded summaries with an ambiguous category, JEV capability `failure-classification` with purpose `bounded-failure-category` may classify them; root-cause debugging remains with GPT. Do not rerun jobs, edit workflows, merge, enable auto-merge, approve, or use raw shell/`gh` mutation. End with a human handoff stating the run/commit, earliest causal evidence, local/advisory/final status separately, and any missing evidence or authorization.

Reruns, workflow edits, artifact downloads and platform mutations require the user's scope and authority. Treat infrastructure failures, flaky tests and product defects as separate dispositions; do not infer flakiness from a single retry.
