---
name: agent-maintenance
description: Maintain installed toolkit instructions, skills, agents or configuration.
---

# Agent Maintenance

Use `sdeveng doctor --json` and `sdeveng install --dry-run --json` to inspect ownership and conflicts. Edit the central checkout, validate affected skills and tests, then use `sdeveng update --dry-run --json` before `sdeveng update --json`. Do not overwrite user files or expand global instructions for one project's rules. Check references after renames. Uninstall preserves replacements.
