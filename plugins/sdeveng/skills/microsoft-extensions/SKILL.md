---
name: microsoft-extensions
description: Apply version-matched Microsoft.Extensions hosting, DI, configuration, options, logging, channels, localization, and secrets hygiene in .NET services.
id: microsoft-extensions
version: 3.0.0
activation: '[{"id":"microsoft-extensions"}]'
resources: '[{"path":"references/ai.md","type":"reference","hash":"sha256:4276f462365a8534bee76f88c6772d3ad2f0af491f401d7802670a4e3f57f394","activation":[{"id":"chat-client-registration","frameworkVersion":"10.0.0"}]},{"path":"references/hosting.md","type":"reference","hash":"sha256:28da705488cae29f050a8b7651170727e9f0dbbff741a6e38914e9e59bb9c5d5"},{"path":"references/http-client-resilience.md","type":"reference","hash":"sha256:9d9fdf8be5e527bbf806d570f0a185ad65a9667ad2da7ee3c94aee374620cba6","activation":[{"id":"http-client-resilience"}]},{"path":"references/caching.md","type":"reference","hash":"sha256:bd47f7aca0e7f04f0469890b77cca01125f8859be85ee6d1ce2f5b9fdfcdab1a","activation":[{"id":"memory-cache"}]},{"path":"references/telemetry.md","type":"reference","hash":"sha256:c51c9d3b26e4175c8f5bbacc9fab2e08fb2ac4d4a413233574e043cbd33a5f03","activation":[{"id":"telemetry-logging"}]},{"path":"references/channels.md","type":"reference","hash":"sha256:ddf8a5a65976a0b803cb2cc14ae010222027bade76d4ca32845fabbfa56a1bd6","activation":[{"id":"channels"}]},{"path":"references/localization.md","type":"reference","hash":"sha256:42781d482bc164bd145de5d3baaf8fb5dba78ae9592f6bc52d9f21d10c756ef3","activation":[{"id":"localization"}]},{"path":"references/secrets-configuration.md","type":"reference","hash":"sha256:07f174fca898d627f621d835a0a28b620107bb2fa07ed7b85cfbde79af160d26","activation":[{"id":"configuration"}]}]'
---

# Microsoft.Extensions 10

Use this guidance only when `sdeveng dotnet inspect --json` reports resolved Microsoft.Extensions packages at major version 10. Missing, unknown, mixed, or other major versions do not qualify; check the resolved package/API documentation before making version-specific claims.

Create one Generic Host with `Host.CreateApplicationBuilder`, configure its `Services`, `Configuration`, `Logging`, and `Options`, then build and run that host. Keep application services in its `IServiceCollection`; avoid building extra service providers during registration because they create separate singleton graphs and can hide lifetime errors.

Choose lifetimes by ownership: singleton for thread-safe process-wide state, scoped for one request or work unit, and transient for cheap stateless objects. Do not inject scoped services into singletons. Register options with `AddOptions<T>().Bind(...)` or `Configure<T>(...)`; validate important settings at startup and inject `IOptions<T>` for stable values or the appropriate monitor/snapshot abstraction when reload or scope behavior is required.

Bind configuration through `IConfiguration` sections and typed options. Keep secrets in an approved secret provider, not checked-in settings. Configure logging on the same host builder, select providers deliberately, and use `ILogger<T>` at the owning class; logger calls alone do not establish provider configuration.

Static `sdeveng dotnet inspect` facts identify resolved Host, DI, configuration, options, and logging API calls with source locations. `unknown` means compilation or dynamic/reflection wiring prevents a static conclusion; installed packages alone do not prove use. Multiple service collections or `BuildServiceProvider` calls deserve review, but static facts do not prove runtime host identity, resolution, or behavior.

Load [hosting reference](references/hosting.md) for version-matched upstream documentation.

Load [application AI detection](references/ai.md) only for supported static `chat-client-registration` evidence. The `microsoft-extensions-ai` package fact alone does not prove registration or runtime activity.

`background-service` and `hosted-service` facts require resolved Microsoft.Extensions 10 type identity and static `AddHostedService<T>` or singleton `IHostedService` registration. Unregistered types and matching names do not confirm composition. These facts do not prove startup or shutdown correctness. Pass `ExecuteAsync`'s stopping token to waits and work, honor `StopAsync` cancellation, and let the single Generic Host stop and dispose owned services.

`http-client-factory` identifies resolved `AddHttpClient` registrations; `http-client-typed` and `http-client-named` distinguish generic typed registrations and overloads whose first non-receiver argument is a name. These are static registration facts only: they do not prove a client was requested or that its configuration runs. Keep `HttpClient` instances factory-managed, avoid capturing transient typed clients in singletons, and pass cancellation tokens through outbound requests.

`memory-cache` and `distributed-cache` identify resolved `AddMemoryCache` and `AddDistributedMemoryCache` registrations. Package presence does not establish registration or runtime use. Keep cache keys bounded and avoid embedding secrets or unbounded user input; static facts do not establish eviction or provider behavior.

The resilience reference is available only when static inspection detects the supported `Microsoft.Extensions.Http.Resilience` package. Package presence does not prove a pipeline is configured.

Load [caching](references/caching.md) only for a detected supported memory-cache registration. `telemetry-logging`, `telemetry-tracing`, and `telemetry-metrics` identify resolved OpenTelemetry 1 logging provider and tracing/metric composition calls with locations; installed packages and static configuration do not prove runtime emission or export.

Load [telemetry](references/telemetry.md) only when static inspection detects a supported telemetry composition fact.

Load [channel guidance](references/channels.md) only when static inspection detects resolved `System.Threading.Channels` API use.

Load [localization guidance](references/localization.md) only when static inspection detects a resolved `AddLocalization` registration.

Load [secrets and configuration hygiene](references/secrets-configuration.md) only when static inspection detects resolved configuration API use.
