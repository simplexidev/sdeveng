# Avalonia static detection cases

Evaluator: `AvaloniaDetectionTests` in `tests/SdevEng.Tests`.
Run `dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj --filter FullyQualifiedName~AvaloniaDetectionTests`.

Independent synthetic API/source fixtures exercise desktop and single-view
lifetime types, XAML, code-only, mixed, unrelated code, unresolved compilation
and unsupported/unresolved versions. Expected facts are fixed independently of
the detector. The synthetic API shape cites Avalonia 11.3.0 revision
`d6edb46ce04f983892a61d3abf906014d3f5ec8d`; it does not qualify real assets.
Temporary projects reach both `repo describe` and `dotnet inspect`, evaluating
XAML items and resolving Roslyn symbols from synthetic assemblies. Machine
outputs validate against the capability and repository-description schemas.
No desktop display, UI execution, model call or scaffold compilation is used.
