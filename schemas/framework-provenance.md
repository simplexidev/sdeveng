# Framework provenance compatibility

`dotnet-skills-provenance.schema.json` remains the canonical inventory schema.
Version 2 is unchanged. Version 3 retains every version-2 field and requires
`frameworks`; `FrameworkProvenance.Read` validates the versioned entries before
`DotnetSkillsDrift` uses the inventory. Reading returns a copy and performs no
network access, writes, migration or reference refresh.

Each entry declares a canonical framework ID (`dotnet`, `microsoft.extensions`,
`system.commandline`, `avalonia`, `terminal.gui`), explicit aliases, supported
positive major versions, HTTPS source URI, pinned revision, SHA-256 hash,
capture timestamp with timezone, and consuming skill IDs. Unknown major versions
or hashes use the literal `unknown`; they must never be inferred from a source
repository revision. Aliases and framework IDs cannot collide, including across
entries. The reader checks these cross-entry constraints beyond schema shape.
Consumer IDs identify references; they do not introduce or load skills.

The version-3 fixture is synthetic contract evidence, not a refreshed source pin
or framework qualification. The checked-in upstream inventory stays version 2;
migration and additional runtime bindings are deferred. Existing drift output
and changed-path classifications remain compatible, with automaticAction=none.
