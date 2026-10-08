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

`generic-host` and `dependency-injection` add project-wide static facts for
Microsoft.Extensions 10. Resolved Host factory/builder calls and constructors
identify Host composition; IServiceCollection Add/TryAdd singleton, scoped,
transient, keyed and descriptor calls identify registrations. These facts also apply to libraries.
Locations contain path and one-based line/column (first 64 matches). Versions
come from evaluated package references or resolved assembly identities.
Installed-but-unused packages yield absent, while incomplete/unsupported
compilations, unsupported versions and indirect/reflection-only wiring yield
unknown. A detected fact proves source composition, not execution or a shared
runtime container; multiple containers remain separately located. Logging detection
is deferred. Existing four- and six-fact records remain schema-compatible.

`configuration` detects resolved IConfiguration GetSection calls (including the
standard ConfigurationManager/Root/Section implementations) and ConfigurationBinder
Bind/Get/GetValue calls. `options` detects AddOptions, Configure/PostConfigure,
OptionsBuilder configuration and configuration-extension Bind/BindConfiguration
calls. Both use the same Microsoft.Extensions 10 version gate, bounded locations
and absent/unknown rules through both discovery commands. These project-wide
source facts do not prove that a configuration source loaded or options were
resolved at runtime; installed packages alone do not prove binding.
