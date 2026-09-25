# Targeted test quality checks

Use only for an explicit quality review or an unclear candidate.

1. Map each public outcome to a concrete witness and an assertion that would
   fail for a realistic defect.
2. Treat unawaited async work, assertion-free executable tests, swallowed
   exceptions, unreachable assertions, and unverifiable mock setup as possible
   false-pass defects.
3. Check the nearest valid/invalid boundary, error propagation, denial/no-op
   paths, and independent returned fields or side effects. Do not reward test
   count or assertion count by itself.
4. Calibrate fixtures, parameterization, snapshots, exception assertions, mock
   verification, and integration resources against repository intent before
   calling them smells.
5. Omit equivalent or private-only mutation ideas. Recommend a test only when a
   caller-visible observation differs.

Provenance: original compact synthesis from `dotnet/skills` commit
`4ed5f7c121da8dd31af31a35cef05070948c6556`, MIT, copyright .NET Foundation
and Contributors; source paths
`plugins/dotnet-test/skills/assertion-quality/SKILL.md`,
`plugins/dotnet-test/skills/test-gap-analysis/SKILL.md`,
`plugins/dotnet-test/skills/test-anti-patterns/SKILL.md`, and
`plugins/dotnet-test/skills/test-smell-detection/SKILL.md`. No upstream text or
code is redistributed verbatim.
