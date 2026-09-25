---
name: ci-triage
description: Diagnose a failed GitHub Actions run or job from bounded workflow evidence.
---

# CI Triage

Use `sdeveng github actions --json` to list recent runs, then `sdeveng github actions --run-id NUMBER --json` for normalized jobs and failed steps. Fetch `--failed-logs` only for the selected run; start with the earliest causal failure rather than downstream cancellations or repeated errors. Correlate the failing command with the exact revision and relevant local configuration before proposing a fix. If deterministic evidence leaves at most five sanitized bounded summaries with an ambiguous category, JEV capability `failure-classification` with purpose `bounded-failure-category` may classify them; root-cause debugging remains with GPT.

Reruns, workflow edits, artifact downloads and platform mutations require the user's scope and authority. Treat infrastructure failures, flaky tests and product defects as separate dispositions; do not infer flakiness from a single retry.
