# Product run event contract v1

A product `runId` is a canonical lowercase UUID, generated once when the run is
created and retained through every resume. It is distinct from a GitHub Actions
run ID and from roadmap Phase/Stage/Step identifiers.

Each line of a run event stream is one JSON object validated by
[`run-event.schema.json`](../../schemas/run-event.schema.json). Events are
immutable and append-only. `sequence` starts at 1 and increases by exactly one
within a run; `(runId, sequence)` is the event identity. A writer must durably
append an event before treating its transition or external identifier as
recorded. Resume replays events in sequence order and rejects gaps, duplicates,
or a run ID mismatch. The local store also rejects malformed event fields,
unknown event types, and transitions whose `fromState` differs from the last
recorded state; it refuses to append to an invalid stream. `occurredAt` is an
RFC 3339 timestamp; sequence, rather than timestamps, determines replay order.

Use `sdeveng run status <UUID>` to read the current state and recorded external
identifiers, or `sdeveng run explain <UUID>` to read the ordered event timeline.
Both commands read `.sdeveng/runs/<UUID>` under the selected `--root` and do
not modify the event store.

Use `sdeveng run list` to list recorded runs in ordinal run-ID order. Use
`sdeveng run abandon <UUID>` to append an `abandoned` transition to an existing
nonterminal run. Missing runs and runs already in a terminal state are rejected.

`state-transition` records `fromState` and `toState`. The first transition uses
`fromState: null`; later transitions name the previous state. State names are
nonempty strings so future workflow states can be introduced without changing
this event version. `external-identifier-recorded` records the issuing
`externalSystem`, its `identifierType`, and the opaque `identifier` value.
Legacy `operation-completed` records `branch-created` or `branch-pushed`.
New start-work milestones use `start-work-progress` with `progressVersion: 1`,
one of `branch-created`, `branch-pushed`, `bootstrap-created`, `pr-created`,
`pr-linked`, or `metadata-persisted`, and status `completed`,
`retryable-failure`, or `terminal-failure`. Detail is redacted and limited to
512 characters. Completed milestones are skipped on resume. External failures
after owned progress enter `STARTING_RETRYABLE`; ownership or invariant failures
enter `STARTING_FAILED`. No automatic rollback removes a branch or remote ref;
unproven or unmerged work is preserved for recovery. The branch identifier is
recorded before branch creation completion.
Record an external identifier before any later action depends on it.
GitHub issue and pull request identifiers use `externalSystem: "github"` with
`identifierType: "issue"` and `"pull-request"`, respectively.
Commit evidence uses `externalSystem: "git"` with `identifierType: "commit"`. Legacy `step-commit` events remain readable;
CI runs use `externalSystem: "github-actions"` with `identifierType: "ci-run"`.
`StartWorkCoordinator` records the canonical origin repository (`git:repository`)
and source issue (`github:issue`) on an existing `created` product run after
validating the clean Git worktree, chosen ref and branch, exact base commit SHA,
and source issue ownership. It then appends `created` → `STARTING`. This state
records preparation only; it does not assert that a branch was created, pushed,
or linked to a pull request.

Examples:

`RunEvidenceExpansion.Expand` is the callable application service for one bounded
repository excerpt. Callers supply the owned repository and run event roots,
canonical Run UUID, current pack ID/revision, indexed items, remaining line
allowance, and optional stable request ID. The request Run must match before
reading or writing. A parent request ID rejects nested expansion before reader
access. Named sections are explicitly omitted as unsupported.

One terminal `evidence-expansion` event records the request ID/digest, role,
pack ID/revision, evidence/source identity, range/section, reader status
(`accepted` means expanded), reason, bytes and unavailable token measurement
(`actualTokens: null`, `tokenMethod: "unavailable"`). Invalid or unavailable
metadata is null; excerpt text is never logged. Legacy expansion events without
the additive digest/revision/section/rejection fields remain readable.

Sequential retries use the canonical Run and stable request ID plus a SHA-256
digest of the canonical request, current pack/source tuple and effective line
allowance (capped at 200). Missing request IDs derive from that digest. A matching
completed retry returns recorded metadata with `Replay: true` and
`ContentAvailable: false`, without rereading or appending. A conflicting digest
returns `ConflictingReplay` without changing history. Persistence failure throws
instead of returning expanded success. Worker routing is deferred; no expansion
CLI is introduced here.

Focused executable evaluation cases in `RunEvidenceExpansionTests` cover
accepted content and schema-valid persistence; rejected ranges, allowances,
pack/item IDs and nested requests; unsupported sections, stale sources, unsafe
paths and byte/line budgets; sequential and conflicting retries; foreign or
malformed Run IDs; and persistence failure. Existing validator, reader and event
compatibility cases remain in `EvidenceExpansionRequestTests` and
`LocalRunEventStoreTests`.

```json
{"schemaVersion":1,"runId":"123e4567-e89b-42d3-a456-426614174000","sequence":1,"occurredAt":"2026-09-28T12:00:00Z","eventType":"state-transition","fromState":null,"toState":"created"}
{"schemaVersion":1,"runId":"123e4567-e89b-42d3-a456-426614174000","sequence":2,"occurredAt":"2026-09-28T12:01:00Z","eventType":"external-identifier-recorded","externalSystem":"github","identifierType":"pull-request","identifier":"42"}
```
