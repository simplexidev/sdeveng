# .NET test framework edge cases

Load only when nearby tests do not establish the needed API or lifecycle rule.

- Resolve the installed framework and assertion-library versions before using a
  newer assertion or attribute. Existing project conventions beat generic
  examples.
- Await asynchronous operations and asynchronous assertions. A created but
  unobserved task can make a test pass falsely.
- Assert the public observation: returned value, exception type and relevant
  metadata, state transition, or external interaction. Avoid assertions about
  private structure.
- Parameterize meaningful input partitions; do not turn unrelated behaviors
  into one branching test. Keep setup deterministic and make external resources
  explicit.
- Framework lifecycle and parallelization hooks have different ownership and
  cleanup rules. Copy the repository's established fixture pattern unless the
  task explicitly changes it.

Provenance: original compact synthesis from `dotnet/skills` commit
`4ed5f7c121da8dd31af31a35cef05070948c6556`, MIT, copyright .NET Foundation
and Contributors; source paths
`plugins/dotnet-test/skills/writing-mstest-tests/SKILL.md`,
`plugins/dotnet-test/skills/code-testing-agent/SKILL.md`, and
`plugins/dotnet-test/skills/code-testing-extensions/SKILL.md`. No upstream text
or code is redistributed verbatim.
