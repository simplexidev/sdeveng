# Microsoft.Extensions 10 caching

Load for a detected `memory-cache` registration; package presence alone does not prove configuration or runtime use. Verify APIs against the resolved major version 10. `AddDistributedMemoryCache` stores data in this process, not a shared external provider.

Keep cache ownership with the host: do not capture scoped values in singleton caches or dispose injected caches. Bound key length and cardinality, avoid raw user input and secrets, and set expiration and appropriate size limits. Coordinate population to avoid duplicate expensive work; cached values must be safe for concurrent readers. Pass cancellation to asynchronous distributed-cache operations and underlying work; cancellation of one caller should not corrupt shared entries.

Log outcomes without keys, payloads, credentials, or personal data. Trace cache operations with bounded attributes and measure hits/misses with low-cardinality labels; configuration does not prove emitted or exported telemetry.

References: [memory caching](https://learn.microsoft.com/dotnet/core/extensions/caching), [distributed caching](https://learn.microsoft.com/aspnet/core/performance/caching/distributed). Match APIs to installed packages; these links do not vendor upstream content.
