# Framework provenance compatibility

`dotnet-skills-provenance.schema.json` remains the canonical inventory schema.
Version 2 is unchanged. Version 3 retains every version-2 field and requires
`frameworks`; `FrameworkProvenance.Read` validates the versioned entries before
`DotnetSkillsDrift` and `upstream status` use the inventory. Reading returns a copy and performs no
network access, writes, migration or reference refresh.

Direct consumers are the upstream command module (status), `DotnetSkillsDrift`
(status/diff/check and drift/refresh), and the pinned-inventory metadata assertion.
All use the compatible reader. Raw test fixture reads/writes intentionally build
inputs for validation; there is no production inventory writer. The historical
filename, schema path, command names and status `plugins` field remain compatible.
Status returns the entire validated inventory, preserving v2/v3 source/version
facts, aliases and unknowns; invalid versions or entries fail closed.

Each entry declares a canonical framework ID (`dotnet`, `microsoft.extensions`,
`system.commandline`, `avalonia`, `terminal.gui`), explicit aliases, supported
positive major versions or the literal `unknown`, HTTPS source URI, pinned revision, SHA-256 hash,
capture timestamp with timezone, and consuming skill IDs. Unknown major versions
or hashes use the literal `unknown`; they must never be inferred from a source
repository revision. Aliases and framework IDs cannot collide, including across
entries. The reader checks these cross-entry constraints beyond schema shape.
Capture time also permits `unknown` when only a historical inspection date is
available. A source commit date is not a capture timestamp.
Consumer IDs identify references; they do not introduce or load skills.

The version-3 fixture is synthetic contract evidence, not a refreshed source pin
or framework qualification. The checked-in upstream inventory is migrated to
version 3 with an explicit `.NET` alias and its original source URI/revision.
All historical snapshot, plugin versions, decisions and license facts remain
unchanged; the legacy fixture preserves that baseline. Supported majors are
recorded only where the repository has an explicit target/API profile:
.NET 10 from the `net10.0` target and Microsoft.Extensions 10 from its pinned
10.0.12 package. Other majors are not inferred from release listings. The
System.CommandLine, Avalonia and Terminal.Gui boundaries remain `unknown` until
their profiles are verified. Source hash and capture time remain `unknown`,
without inventing a timestamp from the inspection date. Additional framework
runtime bindings are deferred. Existing drift output
and changed-path classifications remain compatible, with automaticAction=none.

`upstream dotnet-skills drift|refresh [--observations-file <path>]` compares local
observations only. `refresh` emits a PLAN with current pins, observed references,
classifications and reasons; it never updates inventory, installs code or loads
documentation. `--dry-run` has the same read-only result. Existing status/diff/check
and changed-path output remain unchanged; observations-file is rejected for those
operations. Output uses `framework-drift-plan.schema.json`; input uses
`framework-observations.schema.json`, version 1, at most five entries and 16384
UTF-8 bytes. IDs must be canonical (inventory aliases are preserved, not inferred)
and source URIs must exactly match the registry authority. Each observation has
capture time and explicit provenance (fixtures say synthetic), nullable revision,
SHA-256 hash and major, and a reason when any fact is unavailable. Duplicate IDs,
unknown IDs, mismatched sources and malformed values fail closed.

Only known facts from the same authority compare. Known changed revision/hash
requires review; a known major outside a known supported set is unsupported.
Unknown pins, support boundaries or observed facts remain unknown; absence of an
observations file never means no drift. Current requires equal known pins and a
known supported major. V2 uses its historical dotnet snapshot; other frameworks
remain unknown. Existing upstream releases do not establish toolkit support.
