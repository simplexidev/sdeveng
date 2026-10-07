---
name: api-compatibility
id: api-compatibility
version: 3.0.0
activation: '[{"id":"public-api-change"}]'
resources: '[{"path":"references/symbol-aware-api-compatibility.md","type":"procedure","hash":"sha256:8ba59b4c07f428bb86adec825f89dc8a4cede08008d5f1ef94a5be441ef87529"}]'
description: Check public .NET API compatibility after an actual public surface change.
---

# Api Compatibility

Use this skill only after an actual public API change is requested or appears in a bounded change. Follow the [symbol-aware API compatibility procedure](references/symbol-aware-api-compatibility.md) to anchor and review the change. Preserve public API unless acceptance explicitly authorizes a change. Do not automatically accept generated API baselines; intentional breaks require migration and versioning decisions.

Load [runtime and advanced .NET routing](../../references/advanced-dotnet-routing.md) only when analyzer or publish evidence selects package validation, trimming/AOT, or ABI/P/Invoke compatibility.
