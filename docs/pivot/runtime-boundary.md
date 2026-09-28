# Roadmap and product runtime boundary

The roadmap runner and the `sdeveng` product are separate systems. External
`workplan.xml`, `workplan.sh`, runner logs, and Phase/Stage/Step execution state
are roadmap-control infrastructure. They are not part of the `sdeveng` runtime,
and the product must not depend on those files or logs.

The product runtime is organized around `Run`, `WorkRequest`, `WorkUnit`,
`WorkerRole`, `EvidencePack`, `PromptManifest`, and `ChangeSet`. Phase 0
Stage 0.2 metadata and status contracts, where present, are bootstrap and
compatibility metadata; they do not define the central runtime architecture.

Product capabilities are introduced in their assigned phases:

- Durable product run and resume state belongs to Phase 1.
- Request condensation, decomposition, and WorkUnit budgeting belong to Phase 8.
- Agent Manager coordination belongs to Phase 8.
- Coder, test, reviewer, and repair execution belongs to Phase 9.

Roadmap Phase/Stage/Step execution state remains with the external roadmap
runner. Product run/resume state is product data and is implemented as part of
Phase 1; the two state systems have no runtime dependency on one another.
