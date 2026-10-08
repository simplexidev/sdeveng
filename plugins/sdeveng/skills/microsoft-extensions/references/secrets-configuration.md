# Secrets and configuration hygiene

Bind non-secret settings through `IConfiguration` sections and typed options; validate required values at startup. Keep credentials in an approved secret provider with least-privilege access, and avoid checked-in settings, command-line arguments, or broadly inherited environment variables as secret stores. Never log, trace, emit as metric dimensions, or include in exceptions secret values or raw environment/configuration dumps. Redact before recording diagnostic context, and pass only the specific non-secret settings each component needs.

Static configuration facts identify resolved API use, not the source or sensitivity of values. Review provider precedence, deployment access, and redaction at runtime.

References: [Options pattern](https://learn.microsoft.com/dotnet/core/extensions/options), [Configuration providers](https://learn.microsoft.com/dotnet/core/extensions/configuration-providers).
