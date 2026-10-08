---
name: api-compatibility
id: api-compatibility
version: 3.0.0
activation: '[{"id":"public-api-change"},{"id":"publish-trimmed"}]'
resources: '[{"path":"references/symbol-aware-api-compatibility.md","type":"procedure","hash":"sha256:8ba59b4c07f428bb86adec825f89dc8a4cede08008d5f1ef94a5be441ef87529"},{"path":"references/trimming.md","type":"reference","hash":"sha256:cbc406160e3ef27bd75d963815c648858ce56bf405d0f1ed817d456c24e8057c","activation":[{"id":"publish-trimmed"}]}]'
description: Check public .NET API changes or review reflection-sensitive calls in trimmed applications.
---

# Api Compatibility

For an actual requested or observed public API change, follow the [symbol-aware API compatibility procedure](references/symbol-aware-api-compatibility.md). Preserve public API unless acceptance explicitly authorizes a change. Do not automatically accept generated API baselines; intentional breaks require migration and versioning decisions.

Load [trimming review](references/trimming.md) only for detected evaluated `PublishTrimmed=true`; reflection facts alone do not activate it or prove AOT safety.

Load [runtime and advanced .NET routing](../../references/advanced-dotnet-routing.md) only when analyzer or publish evidence selects package validation, trimming/AOT, or ABI/P/Invoke compatibility.
