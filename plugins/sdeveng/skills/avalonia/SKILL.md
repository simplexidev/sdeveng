---
name: avalonia
description: Build or review Avalonia 11.3.0 applications using verified lifetime, UI-thread, XAML/code-only, binding, resource, input, accessibility, and shutdown guidance.
id: avalonia
version: 3.0.0
activation: '[{"id":"avalonia","frameworkVersion":"11.3.0"}]'
resources: '[{"path":"references/lifetime-ui-thread.md","type":"reference","hash":"sha256:858e8865e7fde5874a38e00188491bc20508f559a0b7eb5d603cad53dbf92664"}]'
---

# Avalonia 11.3.0

Use this skill only when project inspection reports the resolved Avalonia
package as exactly 11.3.0. Other, missing, or unknown versions do not qualify;
verify matching official documentation before relying on API guidance. The
supported major and API source revision come from the Avalonia entry in
[`dotnet-skills-provenance.md`](../../references/dotnet-skills-provenance.md).

Preserve the application's detected lifetime and style facts. Support both
XAML and code-only composition; do not require XAML. Keep guidance lazy: load
only a version-matched reference declared in metadata for the requested topic.
