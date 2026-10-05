# Chat-template golden fixtures

These UTF-8 files are synthetic, fixture-only templates, not model templates.
They have LF message separators, no BOM, and no trailing newline. Their template
metadata and canonical PromptManifest input are defined in
`ChatTemplateManifestTests.RegistryValidatesSchemaIdentityAndRendersCanonicalMessagesToolsAndPrefix`.

Both fixtures contain the complete rendered messages, ordinally sorted tools,
output contract, and generation prefix. `compact.golden.txt` ends with
`</m><assistant>`; `separated.golden.txt` ends with `</m>`, LF, then
`<m>assistant|`. The renderer appends the prefix verbatim exactly once, so any
separator before generation belongs to the prefix metadata. Message markers,
header/body and message separators, tool-section markers, and the entire
generation prefix are template overhead, distinct from message/tool content.
The existing metadata checksum pins all these fields.

Run the deterministic regression/evaluation cases with:

```sh
dotnet test tests/SdevEng.Tests/SdevEng.Tests.csproj --filter FullyQualifiedName~ChatTemplateManifestTests
```

The suite registers serialized template metadata against a verified temporary
tokenizer asset and compares the combined render entry's text and UTF-8 bytes
against both golden files. It checks repeatability across cultures and tool input
orders, prefix checksum binding, schema validity, unknown template identities,
missing content/assets, and invalid tools/prefixes. It uses no network, inference,
or executable template code.
