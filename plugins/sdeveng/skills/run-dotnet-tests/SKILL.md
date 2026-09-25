---
name: run-dotnet-tests
description: Plan or run the smallest relevant .NET test scope, including one test, class, category, project, or affected tests.
---

# Run .NET Tests

Use AgentTool `dotnet test-plan` before proposing or running a command. Pass one
of `--test`, `--class`, `--category`, or `--filter` when the user requests a
subset; use `--project` for an explicit project/solution, otherwise use
`--base` to select affected test projects. AgentTool owns project discovery,
framework/platform detection, filter translation, argument placement, and the
executable argument arrays.

Run only the emitted commands and only when execution was requested. Default to
the requested or affected scope. Broaden after evidence that the change crosses
a shared boundary, the narrow scope cannot represent the failure, or the user
asks for a wider gate. Do not add packages, restore separately, or substitute a
different runner merely because the first run fails.

After execution, summarize each TRX or JUnit artifact with AgentTool
`test-results summarize`. Report commands actually run, tests executed, and the
first causal failure; never equate build success with test success.

Read [`test-platform-edge-cases.md`](../../references/test-platform-edge-cases.md)
only when AgentTool reports an unconfigured/unknown platform, a raw MTP filter
is rejected, the repository is classic non-SDK, or collection/watch/diagnostic
flags are explicitly needed. VSTest/MTP identity, filters, pass/fail, and scope
are exact facts and never go to JEV.
