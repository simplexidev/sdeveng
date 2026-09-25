# JEV

The client follows TypeSafe's [HTTP quickstart](https://docs.typesafe.ai/introduction/quickstart):
POST https://api.typesafe.ai/v1/systemone with bearer authentication, model, state and
questions. Noul returns probability; Choice selects named criteria; Score uses ordered
levels. Responses are type/range/distribution checked before use. No live API call is
part of the tests or bootstrap validation.

`TYPESAFE_API_KEY` is the only application-level secret input. Deliberately inject it
into the specific `sdeveng` process or narrowly scoped terminal session used for live JEV
work. Secret storage and injection are outside `sdeveng` and the toolkit. Do not place it
in arguments, files, JSON, `.env`, shell profiles, `environment.d`, keyrings, desktop
credential stores, or a desktop-session-wide environment. Optional non-secret overrides
are defined in `config/jev.json` and its schema. `doctor` reports only
`JEV credentials: configured` or `JEV credentials: unavailable`. auto and off always preserve normal Codex behavior;
required reports exit 3 on service failure while still marking the result REVIEW.
Uncertain valid judgments remain REVIEW. No retries can accidentally multiply billing.
Redirects are disabled to avoid forwarding credentials to another endpoint.

Every invocation declares a configured capability and purpose. Policies default to zero
expected calls so JEV is never an always-on tax; a positive maximum is a hard per-command
budget, not a target. Policies also bound bytes/candidates, require deterministic narrowing,
and choose normal or stronger GPT escalation. Unknown purposes and disallowed capabilities
remain local and return REVIEW without HTTP. The allowed capability families are relevance,
PR/SARIF triage, bounded failure/upstream classification, and genuinely ambiguous routing.
Exact repository/Git/dependency facts, exact commands, authorization/security dispositions,
code generation, architecture, and open-ended debugging are explicitly disallowed.

Create a small sanitized JSON input. `capability`, `purpose`, and
`deterministicNarrowed` are local routing metadata and are not sent to JEV:

```json
{"capability":"relevance","purpose":"docs-impact","deterministicNarrowed":true,"state":"README describes build setup","instructions":"Is this relevant to build documentation?"}
```

Run `sdeveng jev noul --input safe.json --dry-run --json`.
To transmit this exact reviewed input, omit --dry-run and add --safe-input. This flag
asserts caller review of the payload, not a guarantee of automated secret detection.
Never send .env content, credentials, complete private repositories or oversized excerpts.
Choice adds a criteria object mapping labels to descriptions. Score adds an ordered
criteria array. Screen accepts the same local routing metadata plus query and candidates
with id and text, and returns an
individual judgment for every candidate. Query, ids and text must be non-empty strings; ids containing a detected secret are refused before any request,
and ids must be unique; malformed screen input keeps every candidate for review. Narrow
candidates before screening.

Noul relevance >= .70 is INCLUDE, <= .10 is EXCLUDE, everything else REVIEW.
Choice/Score require confidence >= .80. Open INCLUDE and REVIEW items. These heuristics
need task-specific evaluation; never use them for security authorization or exact facts.

Live results include structured `instrumentation` with capability/purpose, expected and
maximum calls, counts, reported/minimum confidence, fallback and GPT escalation state,
and `contextAvoidedBytes`. Screening counts bytes only for EXCLUDE candidate text. The
instrumentation sets `payloadCaptured` false and never contains request text, responses,
credentials, or secrets. Dry-run intentionally displays the reviewed outbound request;
it is not an instrumentation log.

Cache hashes include canonical request and endpoint; files contain responses only,
never requests or keys. Default TTL is 24 hours. Set cacheHours to 0 to disable; remove
with jev cache-clear. Cache state is ignored and best-effort. Model aliases can move, so
pin a provider model when repeatability matters. Private answers can still be sensitive.

Normal GitHub CI is keyless and uses fake HTTP responses. The optional `Live JEV integration`
workflow is isolated in the dedicated `jev-integration` GitHub Environment. Configure its
`TYPESAFE_API_KEY` Environment secret in GitHub; the workflow does not create, populate or
read the value. It is available only to the one synthetic smoke-call step, runs by manual
dispatch or a weekly trusted schedule, and never runs for pull requests (including forks).
