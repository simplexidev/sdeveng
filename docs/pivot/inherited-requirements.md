# Inherited pivot requirements

This document is the self-contained product and execution contract for the
software factory pivot. It does not depend on archived pre-pivot prompts or
execution state.

## Repository scope

The roadmap contains exactly five repositories: `sdeveng`, `sdeveng-docs`,
`sdeveng-metrics-tooling`, `sdeveng-metrics-data`, and
`sdeveng-metrics-dashboard`.

## Product and worker model

The product is a local software factory and control plane for constrained
hardware. It provides broad Git, GitHub, repository, build, test, package,
release, and maintenance tooling.

Normal local generative workers are at most 2B parameters. They are supported
by deterministic discovery, Roslyn and repository intelligence, EvidencePacks,
a Context Compiler, compact skills, bounded Local JEV judgment, structured
edits, tests, independent review, and repair.

Triage applies classification labels but does not mark an Issue as started.
`IN_PROGRESS` begins only after the owned branch is pushed, the draft PR exists,
startup metadata is persisted, and the in-progress label is applied.

## Roadmap and product safety contract

The external roadmap runner organizes its own delivery into Phases, Stages,
and Steps. These are bootstrap control terms, not product run identities.
Product CI evidence is bound to each distinct commit SHA; a newer push does
not erase the earlier commit's advisory evidence.

The factory may prepare PRs and releases, but must never merge its own PRs,
self-approve, enable auto-merge, or weaken repository protection. Human review
and merge are mandatory.

## First-release engineering knowledge

Required first-release knowledge includes C#/.NET, Generic Host and
Microsoft.Extensions, Microsoft.Extensions.AI, System.CommandLine and console
applications, Avalonia, Terminal.Gui, worker services, HTTP and resilience,
caching and telemetry, trimming and NativeAOT, and multi-frontend composition.

## Releases and public metrics

Stable releases use `YYYY.MM.DD` with an explicit policy for multiple releases
on the same day.

Public dashboard metrics must be reproducible in free, deterministic GitHub CI
without LLM inference. For supported models, exact tokenizer and chat-template
prompt accounting is allowed and required. Attribute components for system,
role, skills, request, evidence, tools, state, output contract, and template
overhead.

Local or private model evaluation may measure role success, reviewer quality,
repair success, latency, throughput, RAM, quantization, adapters, and training
outcomes. These evaluations are outside the public deterministic metric
contract.

## Learning and skill qualification

Post-release learning is opt-in and privacy-preserving. Sanitize and minimize
contributed examples locally; support preview, consent, retention, and deletion;
continuously collect eligible reviewed examples; and perform periodic gated
training rather than silently updating production weights.

Stable skill knowledge may be internalized into trained adapters or models only
after held-out mastery and regression/forgetting gates pass. Runtime skill text
remains the fallback until that model-skill pair is qualified.
