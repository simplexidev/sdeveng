---
name: system-commandline
description: Build or review a .NET command-line application using the resolved System.CommandLine 2.0.0 package API.
id: system-commandline
version: 3.0.0
activation: '[{"id":"system-commandline","frameworkVersion":"2.0.0"}]'
resources: '[{"path":"references/api-generation.md","type":"reference","hash":"sha256:8681d9a6e4a7203a1880fe488799a30430d32cc9034be301a77e857dcfd223fc"}]'
---

# System.CommandLine 2.0.0

Use this guidance only when `sdeveng dotnet inspect --json` reports the resolved
`System.CommandLine` package as exactly 2.0.0. A missing, unknown, or different
version does not qualify; find version-matched upstream documentation before
making API claims. Do not infer the API from the SDK or target framework.

Keep command construction, parsing, and invocation explicit. Use
`RootCommand`, `Command`, `Option<T>`, and `Argument<T>`; parse through
`Command.Parse(args)` and invoke the resulting `ParseResult` with `Invoke` or
`InvokeAsync`. For reusable application code, keep command creation separate
from process exit handling. Handle cancellation and asynchronous operations
through the async invocation path.

Treat standard input/output redirection as an explicit runtime condition.
Static `console-redirection` evidence means the entry point reads a redirection
property; it does not mean the process is interactive. Never probe the current
terminal in CI. Keep output and prompts usable when streams are redirected;
only request terminal interaction when the user-facing operation requires it.

Load [API generation notes](references/api-generation.md) for the version-specific
API boundary and upstream links.
