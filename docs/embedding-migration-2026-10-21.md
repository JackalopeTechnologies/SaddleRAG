# Google embedding migration — 21 October 2026

The local installation now uses Ollama `embeddinggemma-2:270m` for document and
query embeddings. All 147,222 stored passages across 36 library/version groups
were re-embedded in place using the existing `reembed_library` tool. Document
content was retained; no duplicate database or vector backup was created.

The corrected installed package is
`1.12.3-local.embeddings2.20261021+3fef2b4cee49f95fdac92227187ebc9b79ca7f7a`,
with the self-contained .NET 10.0.13 runtime. The normal Windows service has
automatic startup, its health screen reports healthy, and the installed tray is
running and connected. A search through Codex returned the expected Math.NET
Filtering answer first. Only the local database profile is configured.

## Effective embedding configuration

- Provider: Ollama 0.40.7; model: `embeddinggemma-2:270m`.
- Dimensions: 768; model-specific document and query instructions enabled.
- Request size: at most 16 passages; four threads; truncation disabled.
- Complete-input token capacity: `num_ctx=8192` and `num_batch=8192`.
- Idle model retention: 300 seconds for embeddings; global
  `OLLAMA_KEEP_ALIVE=0` remains in place for Devstral.
- Native Nomic embedding is disabled. DirectML remains enabled for the separate
  `mxbai-rerank-base-v1` search-result sorter.

The full conversion exposed an input-capacity problem that the shorter comparison
passages had not exercised: Ollama rejected individual 2400-character passages
with HTTP 413 despite its advertised context size. Explicit context and internal
token-batch capacity accepted the rejected inputs without truncation. The resumed
run converted the remaining 146,828 passages in 46.92 minutes, including saving
and job overhead: approximately 52.16 passages per second. All remaining jobs
completed successfully.

## Data and software validation

- All 147,222 vectors are finite, nonzero, and 768-dimensional.
- Every group has a successful complete Google re-embedding job; all catalogued
  version records identify the intended provider, model, and dimensions.
- The full non-embedding content fingerprint matches the original:
  `be6380502b94b35c3e841f68e70bfb2002a50a4ce41420fa7264394f594110fb`.
  Typed saves materialized three empty metadata defaults on older rows. The audit
  reconstructed their verified original field shape, retaining the six rows that
  already contained those defaults; it did not edit the database.
- Clean Release solution rebuild: zero warnings and errors.
- Full suite: 2,436 passed, 24 skipped, zero failed; 28 focused embedding-provider
  tests passed. Skips cover optional native fixtures, symlink privileges,
  explicit diagnostics, and CPU-only checks on the GPU build.
- The restored service reports MongoDB, Ollama, and document conversion healthy.
  Earlier unread log notifications remain available for review.
- Automatic idle unloading was observed without sending an unload command.
  Ollama advertised expiry at 15:03:18 local time after the final Codex search;
  its model list was empty at 15:03:22. SaddleRAG and the tray stayed running and
  healthy, and Devstral did not become resident during the observation.

## Complete-search results and remaining work

All 29 complete search requests succeeded with the existing sorter enabled.
Exact expected passages ranked first for 13 questions and within five for 23.
Manual review found valid alternative answers within five for four additional
questions: the NSubstitute callback and partial-substitute examples, SciChart's
`FindVisualChild<T>`, and the Toshiba frequency/detection-band parameters.

Two questions still expose quality gaps:

- NSubstitute return-for-any-arguments: the correct pages lead the combined
  embedding and keyword scores but fall to 16th and 19th after final sorting in
  the expanded diagnostic. The sorter makes its own score authoritative.
- SciChart vertical hit-test coordinate: the exact `HitTestInfo.YValue` passage
  ranks 558th among 48,561 stored Google vectors, outside the normal 50-vector
  candidate pool. Its cosine score is 0.7327391825221335. The full-library setting
  exposes competing passages absent from the small comparison corpus.

These results are not a comparative rerun of the isolated 667-passage embedding
benchmark. Compare whole-library candidate retrieval and final sorting before
selecting a policy change; no sorting policy was changed during this migration.

Two groups also lack catalog records: `h-notifyicon-wpf` (32 passages) and
`automation1.net` (511 passages). All 543 were preserved, converted, and included
in the audit. Content comparison found 32 and 506 exact matches respectively in
their current counterpart libraries; the remaining five passages need review
before any cleanup.

The search-quality and catalog follow-ups remain in
[issue 189](https://github.com/JackalopeTechnologies/SaddleRAG/issues/189).
For future model changes, use the [repeatable re-embedding procedure](ollama-embeddings.md#re-embedding-stored-libraries).
