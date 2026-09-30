# Verification results

[`verification-result.schema.json`](../../schemas/verification-result.schema.json) defines version 1 of a single validation outcome. It records the source (`local` or `hosted`), exact check, passed or failed status, process exit code, and evidence artifact path. Exit code zero means passed; any positive exit code means failed.

Local commands run through `AgentTool.RunArtifact` now include a `verification` object in their existing process report. The report's existing exit code, summary, and artifact remain available. Hosted result mapping is a later step.
