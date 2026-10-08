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

`console-redirection` is detected from entry-point references to the standard
console redirection properties; this is a source fact, not a terminal probe or
proof of interactive readiness. `system-commandline` reports the resolved
package version when present. The `system-commandline` skill is gated to the
resolved 2.0.0 package; unknown and other versions do not activate it. No
terminal probing is performed.
