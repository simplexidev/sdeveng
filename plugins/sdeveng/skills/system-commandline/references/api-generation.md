# System.CommandLine 2.0.0 API generation

This note applies only to resolved package version 2.0.0. The command instance
exposes `Parse`; its `ParseResult` exposes `Invoke` and `InvokeAsync`. Do not
copy the beta4 extension-method invocation pattern or its pre-beta5 member
names. Recheck the migration guide when the resolved version differs.

Sources:

- [Microsoft Learn: System.CommandLine overview](https://learn.microsoft.com/dotnet/standard/commandline/)
- [Microsoft Learn: migration guide to 2.0.0-beta5+](https://learn.microsoft.com/dotnet/standard/commandline/migration-guide-2.0.0-beta5)

The migration guide documents the API simplification beginning in beta5,
including instance parsing and invocation on `ParseResult`.
