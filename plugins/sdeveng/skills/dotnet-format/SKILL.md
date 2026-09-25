---
name: dotnet-format
description: Format changed C# files or verify their formatting.
---

# Dotnet Format

Use `sdeveng dotnet format --project PATH --json` to verify the existing changed .cs file set; pass `--apply` only when formatting is wanted. Confirm target project includes those files. Preserve unrelated edits and inspect the final diff. Whole-repository formatting requires an explicit reason such as a formatting policy migration.
