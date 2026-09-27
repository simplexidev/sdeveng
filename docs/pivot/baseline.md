# Pivot workspace baseline

This document distinguishes the original pre-kickoff capture from the workspace
observed during the Step 0.0.0 rerun and the repaired Stage execution state.

Archived pre-pivot prompt/state folders are optional human history and are not
required by this roadmap or by the product runtime. This roadmap is
self-contained and does not depend on those archived execution artifacts.

## Canonical repositories

The canonical repositories are `sdeveng`, `sdeveng-docs`,
`sdeveng-metrics-dashboard`, `sdeveng-metrics-data`, and
`sdeveng-metrics-tooling`.

## Starting state before Stage kickoff

Captured by the first attempt before switching branches, creating the Stage
branch, or creating its kickoff commit. The recorded branch, HEAD, origin URL,
and clean state remain verifiable in this rerun: the `sdeveng` integration HEAD
and the other four repositories' checked-out states match this capture.

| Repository | Branch | HEAD SHA | Origin URL | Worktree |
| --- | --- | --- | --- | --- |
| `sdeveng` | `develop/v3.0.0` | `48ff062d4b5d8a4ea1b208bbf84b8f2a59b901ba` | `https://github.com/simplexidev/sdeveng.git` | clean |
| `sdeveng-docs` | `develop/v3.0.0` | `24b3d9140ced32157533a99f7f826781ef1b6d8f` | `https://github.com/simplexidev/sdeveng-docs.git` | clean |
| `sdeveng-metrics-dashboard` | `develop/v3.0.0` | `591d6d505e94727de1403e4c4971207402178b48` | `https://github.com/simplexidev/sdeveng-metrics-dashboard.git` | clean |
| `sdeveng-metrics-data` | `develop/v3.0.0` | `3c8e7f6ce5c9a577d7157b10b86b02f5274b5f7b` | `https://github.com/simplexidev/sdeveng-metrics-data.git` | clean |
| `sdeveng-metrics-tooling` | `develop/v3.0.0` | `615bdf95f1ebf4c4fd039c6a1a76e6f7a461aad7` | `https://github.com/simplexidev/sdeveng-metrics-tooling.git` | clean |

## Rerun observation before Stage repair

Observed with clean worktrees before rebasing the owned Stage branch during this
rerun. The `sdeveng` HEAD below is the first attempt's implementation commit on
the incorrectly `main`-based branch; it is recorded as an observation, not as
the intended Stage base.

| Repository | Branch | HEAD SHA | Origin URL | Worktree |
| --- | --- | --- | --- | --- |
| `sdeveng` | `roadmap/p0-s0-pivot-baseline` | `560d2231eff04b8ffb4f55c9b8ad7928fda52c17` | `https://github.com/simplexidev/sdeveng.git` | clean |
| `sdeveng-docs` | `develop/v3.0.0` | `24b3d9140ced32157533a99f7f826781ef1b6d8f` | `https://github.com/simplexidev/sdeveng-docs.git` | clean |
| `sdeveng-metrics-dashboard` | `develop/v3.0.0` | `591d6d505e94727de1403e4c4971207402178b48` | `https://github.com/simplexidev/sdeveng-metrics-dashboard.git` | clean |
| `sdeveng-metrics-data` | `develop/v3.0.0` | `3c8e7f6ce5c9a577d7157b10b86b02f5274b5f7b` | `https://github.com/simplexidev/sdeveng-metrics-data.git` | clean |
| `sdeveng-metrics-tooling` | `develop/v3.0.0` | `615bdf95f1ebf4c4fd039c6a1a76e6f7a461aad7` | `https://github.com/simplexidev/sdeveng-metrics-tooling.git` | clean |

## Repaired Stage execution

`develop/v3.0.0` is the current roadmap integration branch. The owned
`roadmap/p0-s0-pivot-baseline` branch and its draft PR target that branch.
`main` is not the Stage base for this pivot execution. The owned Stage history
contains the non-functional kickoff commit and this baseline document commit
on top of `develop/v3.0.0`.
