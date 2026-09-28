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
