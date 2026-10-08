# Microsoft.Extensions 10 reference links

Use the API pages matching the resolved package version:

- [Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host)
- [Worker services and BackgroundService](https://learn.microsoft.com/dotnet/core/extensions/workers)
- [Dependency injection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection)
- [Configuration](https://learn.microsoft.com/dotnet/core/extensions/configuration)
- [Options](https://learn.microsoft.com/dotnet/core/extensions/options)
- [Logging](https://learn.microsoft.com/dotnet/core/extensions/logging)

These links are references, not a vendored copy of upstream documentation. Verify overloads against the resolved Microsoft.Extensions 10 package API.

For hosted workers, pass `ExecuteAsync`'s stopping token to waits and downstream work. Complete shutdown promptly when cancellation is requested, and let the Generic Host stop and dispose hosted services.
