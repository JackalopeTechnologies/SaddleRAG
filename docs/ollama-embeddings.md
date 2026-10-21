# Ollama embeddings

SaddleRAG can embed through Ollama while keeping the DirectML search-result
reranker. Set `Onnx.EmbeddingEnabled` to false only during an approved migration.
Changing the embedding model, runtime, dimensions, or task instructions requires
re-embedding the affected libraries before using them for search. Matching vector
dimensions alone does not make two models compatible.

The embedding provider sends sequential batches of up to 16 texts, preserves
their order, rejects truncated inputs and malformed vectors, and stops promptly
on cancellation. Only transient transport failures are retried.

## Settings

These are fields under `Ollama`:

| Field | Default | Meaning |
| --- | --- | --- |
| `EmbeddingModel` | `nomic-embed-text` | Exact installed Ollama model name |
| `EmbeddingDimensions` | `768` | Required output size; a mismatch fails the request |
| `EmbeddingUseTaskPrefixes` | `false` | Explicit opt-in to model-specific document/query instructions |
| `EmbeddingBatchSize` | `16` | Texts per request; range 1–64 |
| `EmbeddingKeepAliveSeconds` | `60` | Idle retention; range 0–300 seconds, with zero unloading after each request |
| `EmbeddingThreads` | `4` | CPU threads available to the embedding runner |
| `EmbeddingContextLength` | Model-specific | Positive token capacity sent as both `num_ctx` and `num_batch`; defaults to 8192 for `embeddinggemma-2`, otherwise leaves Ollama's defaults unchanged |

Ollama's internal token batch must accommodate each complete passage. A large
advertised context alone is insufficient: during full-library testing, 2400-character
passages received HTTP 413 with Ollama's defaults. Explicit `num_ctx=8192` and
`num_batch=8192` accepted the complete inputs with truncation disabled. This token
capacity is separate from `EmbeddingBatchSize`, which counts texts per request.

Task instructions are opt-in to preserve the meaning of vectors created by older
SaddleRAG releases. Enable them for a new library or as part of a coordinated
re-embedding. Never toggle this setting against an existing searchable index
without regenerating that index. SaddleRAG does not currently record the prefix
setting in library metadata, so the migration operator must keep the setting and
stored vectors together.

With instructions enabled, `nomic-embed-text` uses `search_document: ` for
documents and `search_query: ` for queries. `embeddinggemma` and
`embeddinggemma-2` use `title: none | text: ` for documents and
`task: search result | query: ` for queries. Tagged versions of these names are
supported. Unknown model families fail with an actionable error instead of
silently using the wrong instructions. Document titles are not supplied by the
current embedding interface, so Google's documented `none` title is used.

Embedding retention is independent of classification and recon requests.
`OLLAMA_KEEP_ALIVE=0` can continue to unload Devstral after each request while a
small embedding model remains available for 60 seconds between requests. No
setting keeps an embedding model loaded indefinitely.

## Re-embedding stored libraries

Use the existing `reembed_library` tool after selecting a new embedding model.
It regenerates vectors from stored passages without downloading or splitting
the documents again, and is safe to repeat.

1. Pause ordinary search and ingestion while changing the provider, model,
   dimensions, or task instructions. Keep the maintenance server available for
   the re-embedding tools and health screen.
2. Install the selected model and apply its settings, including any runtime
   overrides. For `embeddinggemma-2:270m`, use 768 dimensions, task instructions
   enabled, and an 8192-token context. Disable `Onnx.EmbeddingEnabled`; the
   independent DirectML result sorter can remain enabled.
3. Call `reembed_library` for each stored library/version and the intended database
   profile, with `dryRun: false` and no `maxChunks` limit. Run one job at a time.
4. Poll `get_reembed_status` using its returned job identifier. Require a completed
   job, the full passage count, and the intended provider, model, and dimensions
   before continuing. A failed library can be re-embedded again after correcting
   the cause.
5. Verify unchanged document content, complete valid vectors, model metadata,
   and real searches before restoring ordinary service. Include stored passages
   whose catalog records are missing; report that catalog problem separately
   rather than dropping their data.

A model change replaces derived vectors in the existing database. Returning to
an earlier model requires another complete re-embedding before exposing searches.
Whether to make a separate database backup is an operator decision.

The [October 2026 comparison](embedding-comparison-2026-10-21.md) measures isolated
embedding retrieval. It does not establish the quality of complete searches,
which also apply keyword scoring and result sorting. Validate those searches
against the full libraries during each migration.

Sources: [Ollama embedding requests](https://docs.ollama.com/api/embed),
[Nomic model instructions](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5),
[Google model instructions](https://huggingface.co/google/embeddinggemma-2).
