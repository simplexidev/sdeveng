# Framework provenance compatibility

`dotnet-skills-provenance.schema.json` remains the canonical inventory schema.
Version 2 is unchanged. Version 3 retains every version-2 field and requires
`frameworks`; `FrameworkProvenance.Read` validates the versioned entries before
`DotnetSkillsDrift` uses the inventory. Reading returns a copy and performs no
network access, writes, migration or reference refresh.

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
