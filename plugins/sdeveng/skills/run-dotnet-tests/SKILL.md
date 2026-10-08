---
name: run-dotnet-tests
description: Plan or run the smallest relevant .NET test scope, including one test, class, category, project, or affected tests.
---

# Run .NET Tests

Before proposing/running commands, use `sdeveng dotnet test-plan --json`. For
subsets pass one of `--test`, `--class`, `--category`, or `--filter`; use
`--project` for an explicit project/solution, otherwise `--base` for affected
tests. `sdeveng` owns discovery, framework/platform detection, filter
translation, argument placement, and executable arrays.

Check SDK/environment and selected projects' target frameworks, platform,
framework, and command mode. Use recognized versions/modes only. Report unknown
or unsupported toolchains and load the directed edge reference; never guess a
command or claim success.

Run emitted commands only when requested, at requested/affected scope. Broaden
only when evidence crosses a shared boundary, narrow scope cannot represent the
failure, or user asks. Do not add packages, restore separately, or switch
runners just because a run fails.

Summarize each TRX/JUnit artifact with `sdeveng test-results summarize --file
PATH --json`. Report commands and tests actually run plus the first causal
failure; build success is not test success. Missing runs/results or requested
coverage artifacts mean evidence unavailable, not pass. Coverage is separate
from test outcome; use an established provider/command and load the coverage
skill for explicit analysis.

Read [`test-platform-edge-cases.md`](../../references/test-platform-edge-cases.md)
only for unknown platform, rejected raw MTP filter, classic non-SDK, or explicit
collection/watch/diagnostic flags. VSTest/MTP identity, filters, outcomes, and
scope are exact facts; never send them to JEV.
