---
name: reproducible-build
description: Audit or verify deterministic .NET build reproducibility on demand.
---

# Reproducible Build

Inspect evaluated deterministic properties, SDK pin, SourceLink/path mapping and dependency resolution. For requested verification, build the same revision in two isolated paths with identical SDK, configuration and inputs. Compare appropriate outputs/hashes and diagnose differences; deterministic=true alone is insufficient proof. Use upstream guidance and keep costly duplicate builds out of routine edits.
