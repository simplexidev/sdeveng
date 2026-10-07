# Handoff evidence

- **Local validation** describes checks run in the current checkout. Include the revision and actual result; a local pass does not establish hosted status.
- **Advisory forecast** is a read-only estimate such as a conflict forecast. Label it advisory and do not present it as a hosted result.
- **Final CI** is the hosted result for the exact pushed commit. If the commit or hosted result is unavailable, say final CI is unknown.
- **Human decision** names the concrete next decision or authorization and its target. Stop there; do not act on an assumed response.

Use only guarded typed Git/GitHub operations for facts and authorized local preparation. Keep selected targets explicit, preserve unrelated work, and do not bypass a rejection with raw shell or `gh` writes.
