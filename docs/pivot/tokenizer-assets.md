# Tokenizer asset registration

`TokenizerRegistry.RegisterFile(path)` reads and validates a version 1 tokenizer
manifest, verifies every local asset's recorded SHA-256, and dispatches the declared
adapter through the canonical registry. `Register(manifest)` remains supported;
`Get(id)` retains its metadata result. `Resolve(id)` returns the loaded adapter and
rechecks asset availability and checksums before use. No network downloads, model
weights, inference, or fallback estimates are involved.

Both supported adapters require one vocabulary asset. `fixture-v1` loads a tiny
byte vocabulary and always requires `fixtureOnly=true`. `tiktoken-v1` loads UTF-8
base64-token/integer-rank records, with unique tokens and contiguous ranks from zero.
Its `sha256:` revision must match the vocabulary checksum. Encoding and special-token
settings remain manifest metadata; loaded ranks alone do not implement exact
full-input counting or chat templates. Those operations remain deferred.

Existing version 1 metadata without `modelFamily` still reads and registers with
the explicit `legacy-unspecified` identity. New serialized manifests include
`modelFamily` and validate against `schemas/tokenizer-manifest.schema.json`.

Deterministic regression/evaluation cases live in `TokenizerManifestTests`: schema
validation, checksum mismatch and missing assets, post-registration tampering,
fixture-only dispatch, pinned production format dispatch, malformed/duplicate/gapped
ranks, unknown adapters/identities, and legacy metadata replay. All assets are local
temporary fixtures; these tests make no network or inference calls.
