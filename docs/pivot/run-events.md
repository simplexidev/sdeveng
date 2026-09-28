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

`state-transition` records `fromState` and `toState`. The first transition uses
`fromState: null`; later transitions name the previous state. State names are
nonempty strings so future workflow states can be introduced without changing
this event version. `external-identifier-recorded` records the issuing
`externalSystem`, its `identifierType`, and the opaque `identifier` value.
Record an external identifier before any later action depends on it.
GitHub issue and pull request identifiers use `externalSystem: "github"` with
`identifierType: "issue"` and `"pull-request"`, respectively.
Step commits use `externalSystem: "git"` with `identifierType: "step-commit"`;
CI runs use `externalSystem: "github-actions"` with `identifierType: "ci-run"`.

Examples:

```json
{"schemaVersion":1,"runId":"123e4567-e89b-42d3-a456-426614174000","sequence":1,"occurredAt":"2026-09-28T12:00:00Z","eventType":"state-transition","fromState":null,"toState":"created"}
{"schemaVersion":1,"runId":"123e4567-e89b-42d3-a456-426614174000","sequence":2,"occurredAt":"2026-09-28T12:01:00Z","eventType":"external-identifier-recorded","externalSystem":"github","identifierType":"pull-request","identifier":"42"}
```
