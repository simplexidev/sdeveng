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
full-input counting. Tiktoken exact encoding remains unsupported; vocabulary loading
must not be mistaken for encoding support.

Existing version 1 metadata without `modelFamily` still reads and registers with
the explicit `legacy-unspecified` identity. New serialized manifests include
`modelFamily` and validate against `schemas/tokenizer-manifest.schema.json`.

Deterministic regression/evaluation cases live in `TokenizerManifestTests`: schema
validation, checksum mismatch and missing assets, post-registration tampering,
fixture-only dispatch, pinned production format dispatch, malformed/duplicate/gapped
ranks, unknown adapters/identities, and legacy metadata replay. All assets are local
temporary fixtures; these tests make no network or inference calls.

## Full rendered-input measurement

`RenderedInputTokenCounter`, implementing `IRenderedInputTokenCounter`, consumes the
existing `ChatTemplateRegistry` and its verified tokenizer registry. `Count` renders
the complete PromptManifest, including sorted tools, message/control text and the
generation prefix, then calls the tokenizer once with that entire string. It never
sums component tokens or generates a completion. No public command or worker wiring
is added.

The default `RenderedInputTokenPolicy` requires exact counting. Unsupported encoding
returns `unavailable`; only `RequireExact=false, AllowEstimate=true` enables
`ceil(UTF-8 bytes/4)`, labeled `estimated` with method
`ceil-utf8-bytes-div-4`. Missing/tampered assets, missing templates/content and invalid
prompts remain unavailable even when estimates are allowed.

Exact proof currently uses only the explicitly fixture-only `fixture-byte-v1`
encoding: match the longest declared special token (ordinal tie break), otherwise
emit one token per vocabulary byte. Bytes outside the vocabulary are unsupported.
This synthetic encoding does not establish exact support for production model
families. Tiktoken metadata can be rendered and explicitly estimated, but cannot
satisfy exact-required policy.

The version 1 result records kind, count/method or unavailable reason, tokenizer
and template IDs/revisions, vocabulary hashes, template checksum, fixture flag,
rendered UTF-8 byte count and SHA-256 digest. The digest/byte count are unavailable
when complete rendering is unavailable; registered asset attribution survives
asset verification failure. `Artifacts.WriteRenderedInputTokenMeasurement` validates
and persists the result using the existing artifact writer, with contract
`schemas/rendered-input-token-measurement.schema.json`.

Deterministic regression/evaluation proof is `RenderedInputTokenCounterTests`:
the existing compact/separated golden inputs produce exactly 270/266 fixture tokens
from 298/301 bytes. A special token spans message boundaries, proving whole-input
counting. Tests also cover Unicode bytes, tool order/replay, attribution/schema and
artifact replay, unchanged inputs/unrelated files, invalid/absent content/templates,
asset tampering/removal/restoration, unsupported fixture/production encodings and
explicit estimate policy. Run the focused suite with:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj --filter 'FullyQualifiedName~RenderedInputTokenCounterTests|FullyQualifiedName~ChatTemplateManifestTests|FullyQualifiedName~TokenizerManifestTests'
```
