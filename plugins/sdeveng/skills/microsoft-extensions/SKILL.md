---
name: microsoft-extensions
description: Apply version-matched Microsoft.Extensions hosting, DI, configuration, options, and logging guidance in .NET services.
id: microsoft-extensions
version: 3.0.0
activation: '[{"id":"microsoft-extensions"}]'
resources: '[{"path":"references/hosting.md","type":"reference","hash":"sha256:28da705488cae29f050a8b7651170727e9f0dbbff741a6e38914e9e59bb9c5d5"}]'
---

# Microsoft.Extensions 10

Use this guidance only when `sdeveng dotnet inspect --json` reports resolved Microsoft.Extensions packages at major version 10. Missing, unknown, mixed, or other major versions do not qualify; check the resolved package/API documentation before making version-specific claims.

Create one Generic Host with `Host.CreateApplicationBuilder`, configure its `Services`, `Configuration`, `Logging`, and `Options`, then build and run that host. Keep application services in its `IServiceCollection`; avoid building extra service providers during registration because they create separate singleton graphs and can hide lifetime errors.

Choose lifetimes by ownership: singleton for thread-safe process-wide state, scoped for one request or work unit, and transient for cheap stateless objects. Do not inject scoped services into singletons. Register options with `AddOptions<T>().Bind(...)` or `Configure<T>(...)`; validate important settings at startup and inject `IOptions<T>` for stable values or the appropriate monitor/snapshot abstraction when reload or scope behavior is required.

Bind configuration through `IConfiguration` sections and typed options. Keep secrets in an approved secret provider, not checked-in settings. Configure logging on the same host builder, select providers deliberately, and use `ILogger<T>` at the owning class; logger calls alone do not establish provider configuration.

Static `sdeveng dotnet inspect` facts identify resolved Host, DI, configuration, options, and logging API calls with source locations. `unknown` means compilation or dynamic/reflection wiring prevents a static conclusion; installed packages alone do not prove use. Multiple service collections or `BuildServiceProvider` calls deserve review, but static facts do not prove runtime host identity, resolution, or behavior.

Load [hosting reference](references/hosting.md) for version-matched upstream documentation.

`background-service` and `hosted-service` facts require resolved Microsoft.Extensions 10 type identity and static `AddHostedService<T>` or singleton `IHostedService` registration. Unregistered types and matching names do not confirm composition. These facts do not prove startup or shutdown correctness. Pass `ExecuteAsync`'s stopping token to waits and work, honor `StopAsync` cancellation, and let the single Generic Host stop and dispose owned services.

`http-client-factory` identifies resolved `AddHttpClient` registrations; `http-client-typed` and `http-client-named` distinguish generic typed registrations and overloads whose first non-receiver argument is a name. These are static registration facts only: they do not prove a client was requested or that its configuration runs. Keep `HttpClient` instances factory-managed, avoid capturing transient typed clients in singletons, and pass cancellation tokens through outbound requests.
