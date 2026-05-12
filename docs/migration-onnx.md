# Migrating an Existing SaddleRAG Install to ONNX Embeddings + Rerank

This is the operator-facing procedure for flipping a running SaddleRAG
instance from Ollama-backed embeddings to in-process ONNX inference.
The new path uses `Microsoft.ML.OnnxRuntime` to run
`nomic-embed-text-v1.5` (embedding, 768-dim) and
`mxbai-rerank-base-v1` (cross-encoder reranker) directly inside
`SaddleRAG.Mcp` — no separate process, no HTTP IPC, no port management.

## Background

The implementation plan and design rationale live in
[`docs/superpowers/plans/2026-05-12-onnx-embedding-migration.md`](superpowers/plans/2026-05-12-onnx-embedding-migration.md).

Phases 1–3 ship in this PR. Phase 4 (this document) is the verification
procedure. Phase 5 (legacy reranker + query-planner deletion, Ollama
model registry generalization) also ships in this PR.

## TL;DR

1. Edit `appsettings.json`: set `Onnx.Enabled = true` and
   `Onnx.EmbeddingEnabled = true`.
2. Restart SaddleRAG. The warmup will download
   `nomic-embed-text-v1.5` and `mxbai-rerank-base-v1` (~364 MB combined)
   into `%ProgramData%\SaddleRAG\models\onnx\`.
3. For each existing library, run `reembed_library(library=..., version=...)`
   to replace the old Ollama vectors with ONNX vectors. The old vectors
   are dimension-compatible but semantically different, so search will
   produce wrong results until reembed completes.
4. Optionally enable rerank: set `Ranking.ReRankerStrategy = "Onnx"`.

## Prerequisites

- SaddleRAG built with Phase 1–5 changes (this PR).
- Outbound HTTPS to `huggingface.co` from the SaddleRAG host (first
  install only; subsequent restarts reuse the cached model files).
- Disk space for the models: ~273 MB (nomic-fp16) + ~244 MB (mxbai-base
  quantized) = ~517 MB.

## Step 1 — Enable ONNX in config

Edit `SaddleRAG.Mcp/appsettings.json` (or your environment-specific
overlay like `appsettings.Production.json`):

```json
"Onnx": {
    "Enabled": true,
    "EmbeddingEnabled": true
}
```

The registry of available models (`Onnx.EmbeddingModels` and
`Onnx.RerankerModels`) is already populated by the shipped defaults.
You don't have to touch those entries unless you want to add a new
model or change the active one.

To pick a different reranker than the default:

```json
"Onnx": {
    "ActiveRerankerModel": "mxbai-rerank-large-v1"
}
```

To disable reranking entirely (search becomes pure hybrid vector+BM25):

```json
"Onnx": {
    "ActiveRerankerModel": "none"
}
```

## Step 2 — Restart SaddleRAG and watch warmup logs

```
SaddleRAG.Mcp.exe
```

Expected warmup sequence in the logs:

```
[Warmup] T+X.Xs - Starting
[Warmup] T+X.Xs (Xms) - MongoDB profiles discovered
[Warmup] T+X.Xs (Xms) - Ollama bootstrap finished
Downloading https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model_fp16.onnx -> ...\nomic-embed-text-v1.5\model.onnx
Downloaded ...\nomic-embed-text-v1.5\model.onnx (267000 KB)
Downloading ...\nomic-embed-text-v1.5\vocab.txt
Downloading ...\mxbai-rerank-base-v1\model.onnx
Downloading ...\mxbai-rerank-base-v1\spm.model
[Warmup] T+X.Xs (Xms) - ONNX models ready
[Warmup] T+X.Xs (Xms) - Vector indices loaded
[Warmup] T+X.Xs (Xms) - embedding provider warm (onnx/nomic-embed-text-v1.5)
[Warmup] T+X.Xs - Completed
```

First start with `Onnx.Enabled=true` performs the downloads (~30–120 s
depending on network speed). Subsequent starts skip the downloads and
just load the cached files (~1–2 s).

If a download fails (timeout, 4xx/5xx, network blocked), the warmup
logs the exception and SaddleRAG continues with the embedding provider
unable to initialize. Delete the partial files in the model directory
and restart to retry.

## Step 3 — Reembed existing libraries

The old chunk vectors in MongoDB came from Ollama's `nomic-embed-text`
which, despite the similar name, produces different vectors than the
ONNX export of `nomic-embed-text-v1.5`. Search results are nonsense
until you reembed.

For each library in `list_libraries`, run:

```
reembed_library(library="<id>", version="<version>")
```

Then poll status:

```
get_reembed_status(jobId="<job-id-returned-by-reembed>")
```

When complete, `EmbeddingProviderId` will read `"onnx"` and
`EmbeddingModelName` will read the active model name
(`"nomic-embed-text-v1.5"` by default). Vector search against that
library now uses ONNX-produced vectors end-to-end.

For a dry run first:

```
reembed_library(library="<id>", version="<version>", dryRun=true)
```

This reports the chunk count that would be processed without modifying
MongoDB.

## Step 4 — Sanity-check search quality

Run a handful of representative `search_docs` queries against a
reembedded library. Top results should be topically relevant. If
results look like noise, the embedding side is misconfigured — check
that `Onnx.ActiveEmbeddingModel` actually matches a populated entry
in `Onnx.EmbeddingModels` and that the model file is present on disk.

## Step 5 — (Optional) Enable rerank

Once embeddings are healthy, flip rerank on:

```json
"Ranking": {
    "ReRankerStrategy": "Onnx"
}
```

Restart. Warmup will load the reranker's ONNX session in addition to
the embedding session. Reranker model is the one named by
`Onnx.ActiveRerankerModel` (defaults to first entry = `mxbai-rerank-base-v1`).

You can also flip rerank at runtime without a restart via the MCP tool:

```
set_rerank_strategy(strategy="Onnx")
```

(Valid values: `Off`, `Onnx`. The legacy `Llm` and `CrossEncoder`
strategies were removed in Phase 5 of this migration.)

Compare a few searches with rerank on vs off. Reranker latency adds
~150 ms per search for the default `RerankBatchSize=64`.

## Where models live on disk

`%ProgramData%\SaddleRAG\models\onnx\{ModelName}\`

Example:

```
C:\ProgramData\SaddleRAG\models\onnx\nomic-embed-text-v1.5\model.onnx
C:\ProgramData\SaddleRAG\models\onnx\nomic-embed-text-v1.5\vocab.txt
C:\ProgramData\SaddleRAG\models\onnx\mxbai-rerank-base-v1\model.onnx
C:\ProgramData\SaddleRAG\models\onnx\mxbai-rerank-base-v1\spm.model
```

Each model directory is named after the registry entry's `Name`
field. Delete the directory to force a fresh download on next
restart.

## Recovering from a corrupted model file

Delete the file (or the whole model directory) and restart SaddleRAG.
The warmup downloader detects the missing file and re-downloads.
Partial downloads land at `.tmp` next to the target and are atomically
renamed on success — an interrupted download never leaves a half-
written main file in place.

## Going back to Ollama

If for some reason ONNX doesn't work in your environment:

```json
"Onnx": {
    "Enabled": false
}
```

Restart. SaddleRAG falls back to `OllamaEmbeddingProvider`. The library
chunks reembedded under ONNX are now incompatible with the Ollama
embedding provider — you'd need to reembed them again against Ollama,
or restore from a MongoDB backup taken before the migration.

## Customizing the model registry

The `Onnx.EmbeddingModels` and `Onnx.RerankerModels` arrays in
`appsettings.json` are extensible. Each entry has a `Description`
field documenting why it's offered. To add a new model, append a new
entry and (optionally) set the corresponding `Active{...}Model`
selector to its `Name`.

Constraints:
- Embedding entries must use `TokenizerFamily=Bert` for now
  (other families aren't implemented yet).
- Reranker entries can use `Bert` or `SentencePiece` families.
  `XlmRoberta` is recognized but not implemented; selecting it throws
  at startup. Multilingual rerankers like
  `jinaai/jina-reranker-v2-base-multilingual` need XlmRoberta support
  before they can be wired in.
- New entries must specify all required fields per the existing
  defaults (RepoId, ModelFile, TokenizerFamily, plus VocabFile for
  Bert or SpmFile for SentencePiece).

## Driving the registry from the LLM (MCP tools)

The model registry and execution-provider setting are surfaced as MCP
tools so the LLM can inspect and change them without the operator
hand-editing `appsettings.json`.

Read-only:
- `list_embedding_models` — registry + currently active entry.
- `list_reranker_models` — same for rerankers.
- `list_execution_providers` — what's compiled in for this build,
  what the running session loaded with, and any fallback warning.

Mutating (writes to `runtime-overrides.json` next to the executable,
which is registered as a configuration source with higher precedence
than `appsettings.json`; survives restart):
- `set_active_embedding_model(name)` — switches the active embedding
  model. Returns `RequiresRestart=true`. **Invalidates every stored
  vector** — after restart, call `reembed_library` for every library
  reported by `list_libraries`.
- `set_active_reranker_model(name)` — switches reranker. Accepts
  `"none"` to disable. Returns `RequiresRestart=true`. Does not
  invalidate vectors.
- `set_execution_provider(provider)` — sets `Onnx.ExecutionProvider`
  to `Cpu`, `DirectMl`, or `Cuda`. Returns `RequiresRestart=true`.

Immediate:
- `download_onnx_model(name)` — pre-stages a registered model's files
  into `Onnx.ModelsDir`. No restart needed.

The override file is gitignored. To reset to the appsettings.json
values, delete `runtime-overrides.json` and restart.

## GPU acceleration (DirectML / CUDA)

The CPU build (default) ships with `Microsoft.ML.OnnxRuntime` and
runs every session on CPU. To take advantage of a GPU:

1. Build the project with the GPU flavor:

    ```
    dotnet build SaddleRAG.slnx -p:UseGpu=true
    ```

   This swaps the package reference to
   `Microsoft.ML.OnnxRuntime.DirectML` and defines the `USE_GPU`
   conditional-compilation symbol so the runtime can call
   `AppendExecutionProvider_DML` / `AppendExecutionProvider_CUDA`.

2. Set the execution provider at runtime via the MCP tool:

    ```
    set_execution_provider(provider="DirectMl")
    ```

   Or edit `appsettings.json`:

    ```json
    "Onnx": {
        "ExecutionProvider": "DirectMl"
    }
    ```

3. Restart SaddleRAG. The startup logs include `executionProvider=...`
   on the `OnnxEmbeddingProvider ready` and `OnnxReRanker ready`
   lines, reporting which EP actually loaded.

If a GPU EP is requested but the build is CPU-only (or the hardware
refuses), the session falls back to CPU silently and logs a warning.
`list_execution_providers` reports the fallback so the LLM can
explain it to the user:

```json
{
    "CompiledIn": ["Cpu"],
    "ActiveSetting": "DirectMl",
    "ActiveProvider": "Cpu",
    "RequestedProvider": "DirectMl",
    "LastLoadWarning": "ExecutionProvider 'DirectMl' requested but this build is CPU-only (UseGpu=false at build time); falling back to CPU."
}
```

DirectML on Windows works on any DX12 GPU (Intel, AMD, NVIDIA)
without a CUDA install. CUDA support requires a different OnnxRuntime
NuGet (`Microsoft.ML.OnnxRuntime.Gpu`) and the CUDA Toolkit on the
host machine — left as a follow-up because most operator scenarios
are covered by DirectML.
