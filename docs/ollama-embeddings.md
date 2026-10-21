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

The October 2026 comparison uses isolated copies of documentation and does not
change the installed provider, settings, or library vectors. Google is a candidate,
not the active model.

Sources: [Ollama embedding requests](https://docs.ollama.com/api/embed),
[Nomic model instructions](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5),
[Google model instructions](https://huggingface.co/google/embeddinggemma-2).
