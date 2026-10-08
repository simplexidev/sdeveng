# Microsoft.Extensions 10 telemetry

Load only when static inspection detects a supported `telemetry-logging`, `telemetry-tracing`, or `telemetry-metrics` composition fact. Confirm resolved OpenTelemetry package versions and API symbols before applying version-specific guidance. Installed packages do not prove registration; static composition does not prove runtime activity, emitted signals, or export.

Compose logging, tracing, and metrics deliberately on the application host. Instrument the boundaries that answer operational questions, propagate `Activity` context across asynchronous and outbound work, and use stable, low-cardinality metric dimensions. Do not put user IDs, request values, credentials, or other secrets in logs, trace attributes, metric labels, or exception text. Keep logging useful while honoring cancellation and avoiding sensitive payloads.

Check the configured providers/exporters and their resource/service identity separately from package and composition facts. Verify sampling, shutdown/flush behavior, and cancellation against the resolved SDK and hosting integration. Treat static source locations as configuration evidence only; do not claim that signals are being emitted or exported without runtime observations.

References: [OpenTelemetry for .NET](https://opentelemetry.io/docs/languages/dotnet/), [OpenTelemetry .NET documentation](https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/docs). These links reference upstream documentation and are not vendored copies.
