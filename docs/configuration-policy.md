# Configuration policy

## Precedence

When a setting is supplied by more than one source, the higher source in this list wins:

1. Invocation-specific option for the current command.
2. Environment variable.
3. Repository configuration.
4. User configuration.
5. Machine configuration.
6. Built-in default.

Lower-precedence sources provide values for settings that higher-precedence sources do not specify. Secrets belong in an environment variable or another protected credential store, not in checked-in repository configuration.

## Machine configuration directory

The machine-level configuration directory is platform-specific:

- Windows: `%ProgramData%\sdeveng` (normally `C:\ProgramData\sdeveng`).
- macOS: `/Library/Application Support/sdeveng`.
- Linux and other Unix systems: `/etc/sdeveng`.

Machine configuration is intended for administrator-managed defaults shared by users of the machine. User-level configuration is per-user and takes precedence over this layer; repository configuration is project-scoped and takes precedence over user configuration.

## User configuration directory

The user-level configuration directory is platform-specific:

- Windows: `%AppData%\sdeveng` (normally under the user's roaming application data directory).
- macOS: `~/Library/Application Support/sdeveng`.
- Linux and other Unix systems: `${XDG_CONFIG_HOME:-~/.config}/sdeveng`.

## Repository configuration directory

The repository-level configuration directory is `.sdeveng` at the repository root. Configuration files in this directory are scoped to that repository and should be reviewed as repository content.

Operational policy has separate contracts from ordinary settings: context allocation
limits follow `schemas/context-budget-policy.schema.json`, and required continuous
integration checks follow `schemas/ci-policy.schema.json`, and data handling rules
follow `schemas/privacy-policy.schema.json`. These policies do not add settings to
`toolkit-config`.


## Implemented files and overrides

Each optional machine, user, or repository directory may contain `jev.json`,
`output-limits.json`, `repo-health.json`, or `toolkit.json`. The required
`config/` files in the toolkit checkout provide the built-in layer. Properties
from a higher layer replace matching properties from lower layers; absent files
leave the prior values intact. Invalid supplied JSON fails with the source path.

`JEV_MODE`, `TYPESAFE_API_URL`, `JEV_MODEL`, and
`JEV_TIMEOUT_SECONDS` override file values. The same names may be passed with
`--set NAME=VALUE` for one invocation. `config explain` lists loaded files
and shows the effective noncredential settings. `TYPESAFE_API_KEY` is read
only from the protected process environment and is never included in settings
or the explanation.
