---
name: run-dotnet-tests
description: Plan or run the smallest relevant .NET test scope, including one test, class, category, project, or affected tests.
---

# Run .NET Tests

Use `sdeveng dotnet test-plan --json` before proposing or running a command. Pass one
of `--test`, `--class`, `--category`, or `--filter` when the user requests a
subset; use `--project` for an explicit project/solution, otherwise use
`--base` to select affected test projects. `sdeveng` owns project discovery,
framework/platform detection, filter translation, argument placement, and the
executable argument arrays.

Check the plan's SDK/environment facts and each selected project's target
frameworks, test platform, framework, and command mode. Use only versions and
modes recognized by the plan. If planning reports an unknown or unsupported
toolchain/platform, report that limitation and load the edge-case reference
when directed; do not guess a runner command or call the check passed.

Run only the emitted commands and only when execution was requested. Default to
the requested or affected scope. Broaden after evidence that the change crosses
a shared boundary, the narrow scope cannot represent the failure, or the user
asks for a wider gate. Do not add packages, restore separately, or substitute a
different runner merely because the first run fails.

After execution, summarize each TRX or JUnit artifact with
`sdeveng test-results summarize --file PATH --json`. Report commands actually run, tests executed, and the
first causal failure; never equate build success with test success. If a planned
command was not run, results are missing, or a requested coverage artifact is
unavailable, state that evidence is unavailable rather than reporting a pass.
Coverage collection is separate from test pass/fail and must use an established
provider/command; load the coverage skill for explicit coverage analysis.

Read [`test-platform-edge-cases.md`](../../references/test-platform-edge-cases.md)
only when `sdeveng` reports an unconfigured/unknown platform, a raw MTP filter
is rejected, the repository is classic non-SDK, or collection/watch/diagnostic
flags are explicitly needed. VSTest/MTP identity, filters, pass/fail, and scope
are exact facts and never go to JEV.
