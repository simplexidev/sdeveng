# HTTP client resilience

Use `Microsoft.Extensions.Http.Resilience` only when the resolved package version is supported and the project has configured a resilience pipeline. Package presence alone does not prove configuration. Verify available APIs against the installed package version; do not infer them from names.

Keep `HttpClient` instances factory-managed. Avoid capturing transient typed clients in singletons, and pass cancellation tokens through outbound calls. Choose retry, timeout, and circuit-breaker behavior for the operation's semantics; avoid retrying non-idempotent work without an explicit idempotency guarantee.
