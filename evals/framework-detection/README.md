# Console detection regression cases

Deterministic evaluator: `FrameworkCapabilitiesTests` in `tests/SdevEng.Tests`.
Run with `dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj --filter FullyQualifiedName~FrameworkCapabilitiesTests`.

Independent synthetic C# source cases cover top-level and explicit Main entry
points, both Generic Host builder APIs, an installed but unused Hosting package,
uncalled methods/lambdas/local functions, unresolved calls and indirect composition.
Temporary project fixtures evaluate MSBuild and Roslyn through both `repo describe`
and `dotnet inspect`, including a library and unsupported Visual Basic executable.
Fixed expected statuses are independent of the detector. Output is checked against
the capability schema and repository-description contract. No fixture runs a host,
probes a terminal, calls a model or qualifies runtime readiness.
