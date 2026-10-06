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
Its `sha256:` revision must match the vocabulary checksum. Exact counting supports
the complete verified `cl100k_base` vocabulary with SHA-256
`223921b76ee99bde995b7ff738513eef100fb51d18c93597a113bcffe865b2a7`.
Other encodings and incomplete/noncanonical vocabularies remain readable but cannot
count exactly. Known declared special tokens use the cl100k IDs; unknown declarations
or occurrences of undeclared built-in special tokens make counting unavailable.
There is no implicit permission to interpret control tokens.

### Encoding dependency justification

Infrastructure pins [Microsoft.ML.Tokenizers 2.0.0](https://www.nuget.org/packages/Microsoft.ML.Tokenizers/2.0.0)
for its mature .NET tiktoken implementation and interoperability with OpenAI cl100k
vectors; a handwritten regex/BPE encoder would duplicate correctness-sensitive
Unicode segmentation and merge behavior. The adapter uses
`TiktokenTokenizer.CreateForModel("gpt-4", verifiedStream)`; `gpt-4` selects the
library's cl100k encoding and never invokes a model. The stream contains exactly
the bytes verified at registration, so there is no file check/use race or built-in
vocabulary substitution. API and special IDs were checked against the installed
2.0.0 XML docs and [the pinned official source](https://github.com/dotnet/machinelearning/blob/efefa92f4486a43047c5b47618885a71bf7f0967/src/Microsoft.ML.Tokenizers/Model/TiktokenTokenizer.cs).

Tests use [Microsoft.ML.Tokenizers.Data.Cl100kBase 2.0.0](https://www.nuget.org/packages/Microsoft.ML.Tokenizers.Data.Cl100kBase/2.0.0)
to extract the complete embedded vocabulary into temporary local files. Its compact
resource (decompressed SHA-256
`91423dd5a6f4f288c81534b7608e52cc6d54bc7a4bb522b31e9ed9a9b87061be`)
stores base64 tokens in rank order; test setup expands those records to the upstream
token/rank representation and verifies the upstream hash above. No
vocabulary is vendored. Its transitive `Microsoft.Bcl.Memory` is pinned to patched
9.0.14 because 9.0.4 fails NuGet's vulnerability audit
([advisory](https://github.com/advisories/GHSA-73j8-2gch-69rq)).

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

Fixture exact proof uses the explicitly fixture-only `fixture-byte-v1`
encoding: match the longest declared special token (ordinal tie break), otherwise
emit one token per vocabulary byte. Bytes outside the vocabulary are unsupported.
Production exact results use method `tiktoken-v1` with `fixtureOnly=false`.
The independent [OpenAI vectors](https://github.com/openai/tiktoken/blob/main/tests/test_encoding.py)
include empty input (0), `hello world` (IDs 15339, 1917), thumbs-up (9468, 239, 235)
and an explicitly allowed end-of-text marker (15339, 220, 100257).

The version 1 result records kind, count/method or unavailable reason, tokenizer
and template IDs/revisions, vocabulary hashes, template checksum, fixture flag,
rendered UTF-8 byte count and SHA-256 digest. The digest/byte count are unavailable
when complete rendering is unavailable; registered asset attribution survives
asset verification failure. `Artifacts.WriteRenderedInputTokenMeasurement` validates
and persists the result using the existing artifact writer, with contract
`schemas/rendered-input-token-measurement.schema.json`.

Deterministic regression/evaluation proof is `RenderedInputTokenCounterTests` and
`TiktokenCountingTests`:
the existing compact/separated golden inputs produce exactly 270/266 fixture tokens
from 298/301 bytes. A special token spans message boundaries, proving whole-input
counting. Tests also cover Unicode bytes, tool order/replay, attribution/schema and
artifact replay, unchanged inputs/unrelated files, invalid/absent content/templates,
asset tampering/removal/restoration, unsupported fixture/production encodings and
explicit estimate policy. Production cases also cover known token IDs, persisted
schema-valid full-render provenance, unsupported encodings/special declarations,
asset tampering/removal, and source invariants for one complete-input encoding call
with no network, process or inference dependency. Run the focused suite with:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj --filter 'FullyQualifiedName~TiktokenCountingTests|FullyQualifiedName~RenderedInputTokenCounterTests|FullyQualifiedName~ChatTemplateManifestTests|FullyQualifiedName~TokenizerManifestTests'
```
