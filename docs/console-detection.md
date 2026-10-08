# Static console detection

`sdeveng repo describe` and `sdeveng dotnet inspect` project the shared
`frameworkCapabilities` facts for each evaluated project. `plain-console` and
`generic-host-console` use `detected`, `absent`, or `unknown`, with an evidence
reason. These are static facts, not runtime activation or terminal readiness.

C# executable projects use the Roslyn entry point and resolved composition
symbols. A call to `Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder` or
`CreateApplicationBuilder` from that entry point detects Generic Host composition.
An installed package or an uncalled method does not establish composition.
Indirect source calls, incomplete compilations, unsupported executable languages,
and multi-targeted executable projects remain unknown. Non-console output types
are absent. Consumers must only route detected facts to requested capabilities;
unknown facts must not activate framework-specific guidance.

Redirected/interactive assumptions and System.CommandLine version detection and
version-gated skill guidance are deferred. No terminal probing is performed.
