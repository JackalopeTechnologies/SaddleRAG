# ONNX Embedding & Reranking Migration

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Ollama-backed embeddings and reranking with in-process ONNX Runtime inference. Use the same `IEmbeddingProvider` and `IReRanker` abstractions that already exist; the only change at the call sites is which concrete implementation gets registered.

**Architecture:** All ONNX inference runs in-process inside `SaddleRAG.Mcp` via `Microsoft.ML.OnnxRuntime`. No subprocess, no HTTP IPC, no port management, no supervisor service. Tokenization happens in C# via `Microsoft.ML.Tokenizers`. Model files (`.onnx` + tokenizer config) live under `%ProgramData%\SaddleRAG\models\onnx\` and are downloaded by the existing `--prewarm` MSI custom action on first install (same mechanism that warms Ollama today).

**Tech Stack:** .NET 10, `Microsoft.ML.OnnxRuntime` (CPU; `.Gpu` variant available later), `Microsoft.ML.Tokenizers`, `HttpClient` for one-time model downloads, WiX 5 MSI, GitHub Actions, xunit v3, NSubstitute.

**Background — why ONNX and not TEI:** The earlier plan (`2026-05-11-tei-migration.md` then `2026-05-12-tei-migration.md`) targeted HuggingFace Text Embeddings Inference. Local Windows build of TEI v1.9.3 was achieved on this dev machine, but the recipe is fragile: rust toolchain pinned to 1.92.0, Visual Studio Dev Shell required, `CMAKE_GENERATOR=Ninja` workaround for VS 2026, cmake/nasm/ninja prereqs in CI. Every TEI version bump risks re-litigating these. The TEI branch (`feat/tei-migration-revised`) is preserved in case we revisit, but the cost/benefit favors in-process ONNX for a .NET application that's already running on Windows.

---

## Codebase Quick-Reference

| File | Purpose |
|---|---|
| `SaddleRAG.Mcp/Program.cs` | DI wiring. New providers registered here. |
| `SaddleRAG.Mcp/McpWarmupService.cs` | Startup warmup. Probes the active `IEmbeddingProvider`. |
| `SaddleRAG.Mcp/appsettings.json` | Production config. Add `Onnx:` section. |
| `SaddleRAG.Core/Interfaces/IEmbeddingProvider.cs` | Interface: `ProviderId`, `ModelName`, `Dimensions`, `EmbedAsync`. |
| `SaddleRAG.Core/Interfaces/IReRanker.cs` | Interface: `ReRankAsync(query, candidates, maxResults, ct)`. |
| `SaddleRAG.Core/Enums/ReRankerStrategy.cs` | Enum: `Off`, `Llm`, `CrossEncoder`. Add `Onnx`. |
| `SaddleRAG.Ingestion/Embedding/OllamaEmbeddingProvider.cs` | Reference pattern for an embedding provider impl. |
| `SaddleRAG.Ingestion/Embedding/ToggleableReRanker.cs` | Dispatches between reranker impls by strategy. Add `Onnx` branch. |
| `SaddleRAG.Ingestion/Embedding/OllamaSettings.cs` | Reference pattern for a settings class. |
| `SaddleRAG.Mcp/OllamaBootstrapper.cs` and `SaddleRAG.Installer/PrewarmService.cs` | Existing model-download-during-install mechanism. New ONNX downloader follows the same MSI custom-action pattern. |
| `SaddleRAG.Installer/Package.wxs` | MSI. The existing `<Files Include="$(var.PublishDir)\**" />` glob picks up new files in publish/. ONNX runtime DLLs land there via NuGet's runtime-aware publish; model files are downloaded at install. |

---

## Coding Standards (Mandatory)

Apply to every new file:

- **Single return per method.** Declare a result variable, assign it, return at the end.
- **No if/else chains.** Use `switch` expressions.
- **No `continue`.** Use `.Where()` to filter.
- **Field prefixes:** `m` (private instance), `sm` (private static readonly), `pm` (public instance). Constants: PascalCase.
- **Allman braces.** Opening brace on its own line. Single-statement blocks: no braces. Multi-statement: braces.
- **4-space indent**, 120-char line limit.
- **No inline comments.** Comments on their own lines before the code.
- **File header:** First two lines: `// FileName.cs` and the existing copyright + SPDX block (copy from `OllamaSettings.cs`).
- **Regions:** Required for grouped members. Pattern: `#region FieldName property`.
- **XML docs on all public members.**
- **`var` when the RHS makes the type obvious.** Explicit type otherwise.

---

## Hard Bits — Risks That Planning Cannot Resolve

Three questions where the answer comes from running code, not from reasoning about it.

1. **Do the picked models actually have usable ONNX exports?** `nomic-ai/nomic-embed-text-v1.5` advertises an ONNX export (`onnx/model.onnx` in the HF repo); `mixedbread-ai/mxbai-rerank-base-v1` likewise. Phase 1 confirms by loading them with `Microsoft.ML.OnnxRuntime` and running one inference. **Fallback:** if either model's ONNX export is missing, broken, or quality is degraded vs. the original PyTorch checkpoint, swap to known-good alternatives: `sentence-transformers/all-MiniLM-L6-v2` (embedding, 384-dim, established ONNX export) and `cross-encoder/ms-marco-MiniLM-L6-v2` (reranker, established ONNX export).

2. **Will the in-process model load survive the MSI install / warmup window?** Combined model size for nomic + mxbai is ~670 MB downloaded weights. The MSI's `--prewarm` custom action triggers Ollama warming today; the new ONNX downloader fits the same shape. **Fallback:** if download-during-install consistently times out, switch to a lazy-download-on-first-use strategy or ship the .onnx files in the MSI payload (pushes installer size to ~700 MB but eliminates the network dependency).

3. **Does ONNX Runtime + the chosen tokenizer match the original model's output well enough that search quality holds up?** Tokenizer differences (BPE variants, special token handling) can shift embeddings noticeably. Phase 4 verifies by reembedding an existing library and comparing search results to baseline. **Fallback:** if quality regresses, options in order of preference are (a) review tokenizer config, (b) try `FastBertTokenizer` if `Microsoft.ML.Tokenizers` is the culprit, (c) swap to a model with a simpler tokenizer story.

---

## Phase 1 — Spike: Confirm ONNX Models Load and Infer

Before writing a single line of provider code, prove the runtime + tokenizer + model combo actually works. A scratch console app is sufficient.

---

### Task 1: Scratch console app loads nomic embedding ONNX and produces 768-dim vector

**Files:**
- Create: `Scratch/onnx-spike/Program.cs`
- Create: `Scratch/onnx-spike/onnx-spike.csproj`
- Create: `Scratch/onnx-spike/README.md`

This is in `Scratch/` (gitignored) — it's a throwaway proof. We won't keep it. The point is to learn:
- What's the exact input tensor shape ONNX Runtime expects from `nomic-embed-text-v1.5`?
- Does `Microsoft.ML.Tokenizers.BertTokenizer` correctly tokenize the prefix-prompted input?
- Does the output match the model card's stated 768-dim?
- Roughly how fast is single-text embedding on CPU?

- [ ] **Step 1: Create the scratch project**

```
mkdir Scratch\onnx-spike
cd Scratch\onnx-spike
dotnet new console
dotnet add package Microsoft.ML.OnnxRuntime
dotnet add package Microsoft.ML.Tokenizers
```

- [ ] **Step 2: Download nomic ONNX + tokenizer files manually**

From `huggingface.co/nomic-ai/nomic-embed-text-v1.5/tree/main/onnx`, download:
- `model.onnx` (~270 MB)
- `tokenizer.json` (parent dir)
- `tokenizer_config.json` (parent dir)
- `config.json` (parent dir)

Save under `Scratch/onnx-spike/models/nomic/`.

- [ ] **Step 3: Write Program.cs**

Pseudocode (real impl lives in the spike folder, not in this plan):
1. Load `tokenizer.json` via `Microsoft.ML.Tokenizers.BertTokenizer` (or whichever class supports HF tokenizer JSON — confirm during impl).
2. Construct input: `"search_document: hello world"` (the model card specifies a task-specific prefix).
3. Tokenize → input_ids, attention_mask, token_type_ids tensors.
4. Load `model.onnx` via `InferenceSession`.
5. Run inference. Grab `last_hidden_state` output (or `sentence_embedding` if exposed; depends on the export).
6. Mean-pool over tokens using attention_mask, then L2-normalize. (Nomic's standard pooling.)
7. Print the result vector's length and the first 8 values.

**Expect:** length 768, plausible-looking float values.
**If wrong:** Investigate before proceeding. Common failures: wrong tokenizer config, wrong pooling, wrong output tensor name, model expects different input names.

- [ ] **Step 4: Run it and capture output**

```
dotnet run
```

Sample expected output:
```
Loaded nomic-embed-text-v1.5 (270 MB)
Embedded "search_document: hello world" in 47 ms
Vector dimension: 768
First 8 values: 0.0234 -0.0187 0.0412 ...
```

If this all works, ONNX path is viable. Capture the working `Program.cs` content in this plan's Task 1 as a reference for Phase 2's `OnnxEmbeddingProvider` design.

- [ ] **Step 5: Note the outcome in this plan**

Update Hard Bit #1 with the result (resolved / partially resolved / blocked). If the alternative MiniLM model is needed, update the model name throughout the plan before Phase 2.

---

### Task 2: Scratch console app loads mxbai reranker ONNX and ranks 3 candidates

Same project as Task 1; add a second mode.

**Files:**
- Modify: `Scratch/onnx-spike/Program.cs`

- [ ] **Step 1: Download mxbai ONNX**

From `huggingface.co/mixedbread-ai/mxbai-rerank-base-v1`, download the ONNX export (path varies; check the `onnx/` subfolder if present, otherwise check the README for the export instructions). Save under `Scratch/onnx-spike/models/mxbai/`.

- [ ] **Step 2: Extend Program.cs**

For each candidate, run the reranker with the (query, candidate) pair as input. The reranker outputs a single relevance score per pair.

Test query: `"What is the capital of France?"`
Test candidates:
- `"Paris is the capital of France."`
- `"Berlin is the capital of Germany."`
- `"The Seine river runs through Paris."`

**Expect:** Paris-of-France ranks highest; Berlin ranks lowest.
**If wrong:** tokenizer or input shape misconfigured.

- [ ] **Step 3: Note the outcome**

If models work, decide officially: nomic + mxbai are our picks. Otherwise document the fallback (MiniLM pair) and update the rest of the plan.

---

### Task 3: Decide model distribution strategy

Two options, decide before writing the providers.

**A) Ship `.onnx` files in the MSI payload.**
- Pro: install is self-contained; no first-run network dependency.
- Con: MSI grows to ~700 MB. GitHub releases cap at 2 GB per file but 100 MB recommended; pushes us past comfort. Slower MSI download.

**B) Download `.onnx` files at install time via `--prewarm`.**
- Pro: small MSI. Mirrors existing Ollama warming pattern.
- Con: requires network at install time. Adds a failure mode.

Recommendation: **B**. The MSI already runs `SaddleRAG.Mcp.exe --prewarm` as an installer custom action; the new ONNX downloader fits in there. If first-install installs in restricted-network customer environments turn out to be a problem, we can add A as an alternative install mode later.

- [ ] **Step 1: Record the choice in this plan's Key Decisions section**

---

## Phase 2 — Settings, Providers, DI

---

### Task 4: `OnnxSettings` configuration model

Mirrors `OllamaSettings`. Bound from the `Onnx:` section of appsettings.

**Files:**
- Create: `SaddleRAG.Ingestion/Embedding/OnnxSettings.cs`
- Modify: `SaddleRAG.Mcp/appsettings.json`
- Modify: `SaddleRAG.Mcp/Program.cs`
- Test: `SaddleRAG.Tests/Embedding/OnnxSettingsTests.cs`

Properties (default values reflect the Phase 1 decisions):

| Property | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Master switch. When false, providers default back to Ollama. |
| `EmbeddingEnabled` | `false` | Register `OnnxEmbeddingProvider` as the active `IEmbeddingProvider`. |
| `EmbeddingModelId` | `"nomic-ai/nomic-embed-text-v1.5"` | HF repo id used for download bookkeeping. |
| `EmbeddingModelPath` | (computed) | Full path to `model.onnx`. Default: `{ModelsDir}\nomic-embed-text-v1.5\model.onnx`. |
| `EmbeddingDimensions` | `768` | Output dim; must match model. |
| `EmbeddingMaxSequenceLength` | `512` | Token cap. |
| `EmbeddingTaskPrefix` | `"search_document: "` | Nomic's task prefix for document embedding. (Query side uses `"search_query: "`.) |
| `RerankModelId` | `"mixedbread-ai/mxbai-rerank-base-v1"` | HF repo id. |
| `RerankModelPath` | (computed) | Full path to rerank `model.onnx`. |
| `ModelsDir` | `%ProgramData%\SaddleRAG\models\onnx` | Where downloads land. LocalSystem-writable. |
| `IntraOpNumThreads` | `0` (= auto) | ONNX Runtime thread count for intra-op parallelism. 0 lets ORT pick. |

Test that defaults match expected.

---

### Task 5: `OnnxEmbeddingProvider` — `IEmbeddingProvider` impl

**Files:**
- Create: `SaddleRAG.Ingestion/Embedding/OnnxEmbeddingProvider.cs`
- Create: `SaddleRAG.Ingestion/Embedding/OnnxTokenizerLoader.cs` (helper, if tokenizer wiring is non-trivial)
- Test: `SaddleRAG.Tests/Embedding/OnnxEmbeddingProviderTests.cs`

`IEmbeddingProvider` surface (from existing interface):
- `string ProviderId { get; }` → `"onnx"`
- `string ModelName { get; }` → from settings
- `int Dimensions { get; }` → from settings
- `Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)`

Implementation notes:
- Load `InferenceSession` lazily on first call (or eagerly during warmup — pick one, document why).
- Hold the `InferenceSession` for the lifetime of the provider (it's the singleton).
- Hold the tokenizer for the lifetime of the provider.
- For each input text, prepend the task prefix from settings.
- Batch inputs — ONNX Runtime supports batched tensors. Use a max batch size (say 32) and chunk if needed.
- Apply mean-pooling and L2-norm to produce the final vector per text.
- Validate output dimension against `Dimensions`; throw `DataInvalidException` if mismatched.

Tests (using a small synthetic model fixture if practical, otherwise mark as `[Trait("Category", "Integration")]` and skip if model files are missing):
- Provider id and model name surface correctly.
- Embedding a single text returns a vector of the right dimension.
- Embedding 50 texts in one call returns 50 vectors of the right dimension (batching path).
- Embedding an empty list returns an empty list (no inference call).

---

### Task 6: `OnnxReRanker` — `IReRanker` impl + `ReRankerStrategy.Onnx`

**Files:**
- Modify: `SaddleRAG.Core/Enums/ReRankerStrategy.cs` (add `Onnx`)
- Create: `SaddleRAG.Ingestion/Embedding/OnnxReRanker.cs`
- Modify: `SaddleRAG.Ingestion/Embedding/ToggleableReRanker.cs` (add `Onnx` dispatch branch)
- Test: `SaddleRAG.Tests/Embedding/OnnxReRankerTests.cs`

`IReRanker` surface:
- `Task<IReadOnlyList<RerankedCandidate>> ReRankAsync(string query, IReadOnlyList<string> candidates, int maxResults, CancellationToken ct)`

Implementation:
- Cross-encoder model takes `(query, document)` pairs as joint input (one sequence with separator token).
- Run inference once per pair, or batch if model supports.
- Sort by descending score, take top `maxResults`.

Tests:
- Reranks N candidates → returns N (or `maxResults` if smaller).
- Empty candidates → empty result, no inference call.
- Score ordering monotonic for a hand-picked case (Paris > Seine > Berlin for "capital of France").

`ToggleableReRanker` dispatch update: existing `switch` adds `ReRankerStrategy.Onnx => mOnnxReRanker.ReRankAsync(...)`.

---

### Task 7: DI switch in `Program.cs`

**Files:**
- Modify: `SaddleRAG.Mcp/Program.cs`

Replace the fixed `IEmbeddingProvider` registration:

```csharp
builder.Services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();
```

With a conditional registration based on `OnnxSettings`:

```csharp
var onnxSettings = builder.Configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>()
                   ?? new OnnxSettings();

if (onnxSettings.Enabled && onnxSettings.EmbeddingEnabled)
    builder.Services.AddSingleton<IEmbeddingProvider, OnnxEmbeddingProvider>();
else
    builder.Services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();
```

Reranker stays inside `ToggleableReRanker`; just register `OnnxReRanker` as a singleton so the toggleable can resolve it.

```csharp
builder.Services.Configure<OnnxSettings>(builder.Configuration.GetSection(OnnxSettings.SectionName));
builder.Services.AddSingleton<OnnxReRanker>();
```

Build + test:

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
dotnet test SaddleRAG.Tests
```

---

## Phase 3 — Model File Distribution

---

### Task 8: ONNX model downloader

Downloads model files from HuggingFace to `OnnxSettings.ModelsDir` if not already present. Skip if files exist and match the expected `model.onnx` size (rough integrity check).

**Files:**
- Create: `SaddleRAG.Ingestion/Embedding/OnnxModelDownloader.cs`
- Test: `SaddleRAG.Tests/Embedding/OnnxModelDownloaderTests.cs`

Per model, downloads:
- `model.onnx` (the inference graph + weights)
- `tokenizer.json` (HF tokenizer config)
- `config.json` and `tokenizer_config.json` if the tokenizer loader needs them

Source URL pattern: `https://huggingface.co/{repo_id}/resolve/main/{path}`

Atomicity:
- Download to `*.tmp` then rename on success.
- If interrupted, the next prewarm finds the missing/partial file and re-downloads.

Tests (mock `HttpClient`):
- All files present → no network calls.
- Missing files → downloads each, lands in expected path.
- Interrupted download → leftover `.tmp` cleaned up on next run.

---

### Task 9: Wire downloader into the existing `--prewarm` MSI custom action

**Files:**
- Modify: `SaddleRAG.Mcp/Program.cs` (the `--prewarm` branch, near existing `OllamaBootstrapper` invocation)
- Modify: `SaddleRAG.Installer/PrewarmService.cs` (if a timeout adjustment is needed)

When `Onnx.Enabled` is true at install time, the prewarm pass:
1. Calls `OnnxModelDownloader.EnsureModelsAsync(onnxSettings, ct)`.
2. Then constructs an `OnnxEmbeddingProvider`, calls a warmup inference (embed one short string) so the InferenceSession is JIT-compiled. This makes the first real embedding fast.
3. Same for `OnnxReRanker`.

Adjust the prewarm `CancellationToken` timeout to account for download time. Existing TEI-plan adjustment language applies here — scale with a setting like `Onnx.DownloadTimeoutSeconds`.

---

### Task 10: Startup warmup probe uses the active embedding provider

**Files:**
- Modify: `SaddleRAG.Mcp/McpWarmupService.cs`

The existing warmup probe queries `IEmbeddingProvider`. With ONNX wired in, no code change is needed for the probe itself — just verify the log message refers to the provider's `ModelName` rather than hard-coding Ollama's.

Already partially done (per the TEI plan's previous notes about provider-agnostic phase names); confirm it still reads correctly with ONNX as the provider.

---

## Phase 4 — End-to-End Verification with Existing reembed_library

`reembed_library`, `ReembedService`, and the rest of the reembed machinery are already on master (PR #34, commit fe99a63). They work against whatever `IEmbeddingProvider` is registered. Phase 4 is verification, not new code.

---

### Task 11: Flip ONNX on, migrate a test library, confirm search quality

- [ ] **Step 1: Enable ONNX in config**

`appsettings.Development.json`:

```json
"Onnx": {
    "Enabled": true,
    "EmbeddingEnabled": true
}
```

- [ ] **Step 2: Start SaddleRAG (dev)**

Expect logs:
```
[Warmup] T+X.Xs - embedding provider warm (nomic-ai/nomic-embed-text-v1.5)
```

If logs don't show ONNX as the active provider, DI wiring is off — debug before continuing.

- [ ] **Step 3: Pick a small existing library**

`list_libraries` to find one under ~1000 chunks.

- [ ] **Step 4: Dry-run reembed**

```
reembed_library(library="<id>", version="<version>", dryRun=true)
```

Poll `get_reembed_status`. Expect `EmbeddingProviderId="onnx"`, `ChunkCount` matches existing.

- [ ] **Step 5: Real reembed**

```
reembed_library(library="<id>", version="<version>")
```

- [ ] **Step 6: Search quality check**

Run several `search_docs` queries. Compare results to pre-migration baseline (a separate untouched library, or git-saved earlier results). Acceptance: topical relevance held, no nonsense results.

- [ ] **Step 7: Enable rerank**

`Ranking.ReRankerStrategy = "Onnx"`. Restart. Rerun a few queries with rerank on/off and compare ordering.

- [ ] **Step 8: Document migration steps in README or `docs/migration-onnx.md`**

For ops (just the user for now, but make it greppable later):
- How to flip from Ollama to ONNX.
- The `reembed_library` requirement per existing library.
- Where models live on disk.
- How to re-download a corrupted model file (delete + restart).

---

## Spec Coverage Checklist

| Spec requirement | Implemented in |
|---|---|
| ONNX model loads and produces correct-dim embeddings | Phase 1, Task 1 |
| ONNX reranker produces sensible ordering | Phase 1, Task 2 |
| Model distribution strategy decided | Phase 1, Task 3 |
| ONNX configuration seam | Phase 2, Task 4 |
| `IEmbeddingProvider` ONNX implementation | Phase 2, Task 5 |
| `IReRanker` ONNX implementation + `ReRankerStrategy.Onnx` | Phase 2, Task 6 |
| DI switch: Ollama vs ONNX | Phase 2, Task 7 |
| ONNX model downloader | Phase 3, Task 8 |
| MSI install downloads models via existing `--prewarm` action | Phase 3, Task 9 |
| Warmup probe uses provider-agnostic model name | Phase 3, Task 10 |
| Existing `reembed_library` works after provider switch | Phase 4, Task 11 |
| Migration procedure documented | Phase 4, Task 11 Step 8 |

## Key Decisions

1. **Embedding model: `nomic-ai/nomic-embed-text-v1.5`** (768-dim, Apache-2.0). Same model the TEI plan picked; ONNX export is available in the HF repo's `onnx/` directory. Phase 1, Task 1 confirms it loads and produces the right dimension. Fallback: `sentence-transformers/all-MiniLM-L6-v2` (384-dim, very mature ONNX export, smaller and faster but lower retrieval quality).

2. **Reranker model: `mixedbread-ai/mxbai-rerank-base-v1`** (Apache-2.0). Phase 1, Task 2 confirms. Fallback: `cross-encoder/ms-marco-MiniLM-L6-v2` (well-established, fast, MS MARCO-trained).

3. **Tokenizer library: `Microsoft.ML.Tokenizers`** (Microsoft-supported, native NuGet, handles HF `tokenizer.json` for BERT-family models). Fallback: `FastBertTokenizer` (community, faster but less battle-tested) if `Microsoft.ML.Tokenizers` doesn't load the nomic or mxbai tokenizer config cleanly.

4. **Model distribution: download at install (Option B from Task 3).** MSI stays small; existing `--prewarm` mechanism handles the download. Acceptable network dependency for our deployment context. Re-evaluate if customer environments turn out to be air-gapped.

5. **In-process inference, not subprocess.** This is the entire rationale for ONNX over TEI. No supervisor, no port, no IPC. ONNX Runtime is loaded into the SaddleRAG.Mcp process directly.

6. **Why not keep Ollama?** Ollama is a separate process customers must install and run alongside SaddleRAG. The whole migration goal is to eliminate that external dependency and ship inference as part of SaddleRAG itself.

7. **GPU support deferred.** `Microsoft.ML.OnnxRuntime.Gpu` exists and slots in via a different NuGet package, but adds CUDA + cuDNN dependencies and only matters for high-throughput scenarios. Out of scope for v1; flip a NuGet ref later if needed.

8. **Why preserve the TEI branch.** Branch `feat/tei-migration-revised` retains the TEI submodule plus the Windows build recipe we discovered. If ONNX has unexpected blockers in Phase 1, that branch is the documented fallback path. Delete the branch only after Phase 4 is verified working.
