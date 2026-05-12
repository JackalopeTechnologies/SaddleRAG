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

1. **Do the picked models actually have usable ONNX exports?** **Embedding side resolved in Phase 1 spike:** `nomic-ai/nomic-embed-text-v1.5` fp16 ONNX (273 MB) loads via `Microsoft.ML.OnnxRuntime` 1.26 and produces correct 768-dim vectors. **Required workaround:** `SessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC` (the default `ORT_ENABLE_ALL` triggers a `SimplifiedLayerNormFusion` bug against `InsertedPrecisionFreeCast_` nodes). **Reranker side in progress:** `mixedbread-ai/mxbai-rerank-base-v1` uses DeBERTa-v2 + SentencePiece tokenization, requiring a different tokenizer code path than nomic. Phase 1 Task 2 attempts it with `SentencePieceTokenizer.Create()` against `spm.model`. Fallback if mxbai's spike fails: `cross-encoder/ms-marco-MiniLM-L6-v2` (BERT-based, 91 MB, reuses the verified `BertTokenizer` path — older but established).

2. **Will the in-process model load survive the MSI install / warmup window?** Combined model size is ~517 MB with mxbai-quantized (273 MB nomic-fp16 + 244 MB mxbai-quantized), or ~364 MB with ms-marco fallback. The MSI's `--prewarm` custom action already triggers Ollama warming today; the new ONNX downloader fits the same shape. **Fallback:** if download-during-install times out, switch to lazy-download-on-first-use or ship the .onnx files in the MSI payload.

3. **Does ONNX Runtime + the chosen tokenizer match the original model's output well enough that search quality holds up?** Embedding-side tokenization works cleanly via `BertTokenizer`. Reranker-side tokenization is the next thing to validate: `SentencePieceTokenizer` for mxbai needs manual `[CLS] query [SEP] doc [SEP]` framing and manual `token_type_ids` (the C# SP tokenizer doesn't auto-add special tokens like HF's tokenizer.json post-processor does). Phase 4 verifies end-to-end search quality against a real library before declaring this fully resolved.

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

### Task 2: Scratch console app loads mxbai-rerank-base-v1 (DeBERTa + SentencePiece) and ranks 3 candidates

Same project as Task 1; add a rerank section.

**Files:**
- Modify: `Scratch/onnx-spike/Program.cs`

- [ ] **Step 1: Download mxbai ONNX + tokenizer files**

From `huggingface.co/mixedbread-ai/mxbai-rerank-base-v1`, download into `Scratch/onnx-spike/models/mxbai/`:
- `onnx/model_quantized.onnx` (244 MB int8) — save as `model.onnx`
- `spm.model` (the SentencePiece vocabulary, ~2.4 MB)
- `tokenizer_config.json` (for special-token IDs reference)
- `config.json` (architecture confirmation)

- [ ] **Step 2: Extend Program.cs with mxbai rerank**

Tokenization uses `SentencePieceTokenizer.Create()`. The C# SP tokenizer does NOT auto-add specials, so manually frame `[CLS]=1 query [SEP]=2 doc [SEP]=2`. `token_type_ids` is all zeros (DeBERTa-v2 has `type_vocab_size=0`).

```csharp
using FileStream spm = File.OpenRead(spmPath);
SentencePieceTokenizer tok = SentencePieceTokenizer.Create(
    spm,
    addBeginningOfSentence: false,
    addEndOfSentence: false,
    specialTokens: new Dictionary<string, int>
    {
        ["[CLS]"] = 1, ["[SEP]"] = 2, ["[PAD]"] = 0, ["[UNK]"] = 3, ["[MASK]"] = 128000
    });
```

Then for each (query, doc) pair, build the combined int64 input_ids manually and run inference. Output is `logits` shape `[batch, 1]`.

Test query: `"What is the capital of France?"`
Test candidates:
- `"Paris is the capital of France."`
- `"Berlin is the capital of Germany."`
- `"The Seine river runs through Paris."`

**Expect:** Paris-of-France ranks highest; Berlin ranks above Seine because cross-encoders score topical structure (both query and the Berlin candidate share the `"capital of X"` pattern) above lexical overlap (Seine mentions Paris but doesn't answer the question).
**If wrong:** tokenizer or input shape misconfigured.

- [ ] **Step 3: Optionally verify mxbai-rerank-large-v1**

If we want the bigger model on the table, repeat with `onnx/model_quantized.onnx` from the `large-v1` repo (642 MB). Same tokenizer, same code path — just a bigger model file.

- [ ] **Step 4: Record outcome in the plan**

If mxbai works, both `MxbaiBase` and `MxbaiLarge` are viable for `OnnxSettings.RerankModel`. If mxbai fails after honest debugging, fall back to `cross-encoder/ms-marco-MiniLM-L6-v2` (BERT-based, reuses the verified `BertTokenizer` path, much smaller and weaker — only acceptable if the higher-quality options are blocked).

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

Top-level `Onnx` settings (config-driven model registry, design pattern C):

| Property | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Master switch. When false, the embedding provider falls back to `OllamaEmbeddingProvider` and reranking is disabled regardless of the rest of this config. |
| `EmbeddingEnabled` | `false` | When `Enabled=true`, this picks whether `OnnxEmbeddingProvider` (true) or `OllamaEmbeddingProvider` (false) is registered as `IEmbeddingProvider`. |
| `ActiveEmbeddingModel` | `"nomic-embed-text-v1.5"` | Name of the entry in `EmbeddingModels` to use. Missing/empty → first entry. Invalid name → startup error. Cannot be empty/null in practice (embedding is always required when `EmbeddingEnabled=true`). |
| `ActiveRerankerModel` | `"mxbai-rerank-base-v1"` | Name of the entry in `RerankerModels` to use. Missing → first entry (default). Empty string or `null` → no reranking. Invalid name → startup error. |
| `ModelsDir` | `%ProgramData%\SaddleRAG\models\onnx` | Where model downloads land. LocalSystem-writable. |
| `GraphOptimizationLevel` | `"Basic"` | Maps to ORT's `ORT_ENABLE_BASIC`. Required workaround for the ORT 1.26 `SimplifiedLayerNormFusion` bug. Do not change without testing each model in the registry. |
| `IntraOpNumThreads` | `0` (= auto) | ONNX Runtime thread count for intra-op parallelism. 0 lets ORT pick. |
| `RerankBatchSize` | `64` | Max (query, doc) pairs per single ONNX inference call. Batching is what keeps default-on rerank under 200 ms per search. Do not set below 16 in production. |
| `EmbeddingModels` | (see below) | Ordered list of `EmbeddingModelEntry`. **First entry is active.** Swap default by reordering. |
| `RerankerModels` | (see below) | Ordered list of `RerankerModelEntry`. **First entry is active** when `RerankEnabled=true`. |

`EmbeddingModelEntry` fields:

| Field | Purpose |
|---|---|
| `Name` | Stable identifier. Used in download paths (`{ModelsDir}\{Name}\...`) and `IEmbeddingProvider.ModelName`. |
| `Description` | Human-readable explanation of why this model is offered (its strengths, when to pick it). Visible at config time. |
| `RepoId` | HuggingFace repo id (e.g. `"nomic-ai/nomic-embed-text-v1.5"`). |
| `ModelFile` | Path within the HF repo to the ONNX model (e.g. `"onnx/model_fp16.onnx"`). |
| `TokenizerFamily` | Enum: `Bert`, `SentencePiece`, `XlmRoberta`. Adding a new family requires C# code; using a new model within an existing family is config-only. |
| `VocabFile` | Path within the HF repo to vocab/tokenizer file. For `Bert` family, typically `"vocab.txt"`. Empty/null for `SentencePiece` and `XlmRoberta`. |
| `SpmFile` | Path within the HF repo to the SentencePiece model file. Used by `SentencePiece` family. Empty/null for `Bert`. |
| `Dimensions` | Output vector dimension. Must match the model's actual output. |
| `MaxSequenceLength` | Token cap per input. |
| `DocumentPrefix` | Optional task prefix prepended to documents (e.g. nomic's `"search_document: "`). Empty for models that don't need it. |
| `QueryPrefix` | Optional task prefix prepended to queries (e.g. nomic's `"search_query: "`). |

`RerankerModelEntry` fields:

| Field | Purpose |
|---|---|
| `Name` | Stable identifier. Used in download paths and `IReRanker.ModelName`. |
| `Description` | Why this model is offered. |
| `RepoId` | HuggingFace repo id. |
| `ModelFile` | Path within the HF repo to the ONNX model. |
| `TokenizerFamily` | Enum: `Bert`, `SentencePiece`, `XlmRoberta`. |
| `VocabFile` / `SpmFile` | As in embeddings. |
| `MaxSequenceLength` | Max combined `[CLS] query [SEP] doc [SEP]` length per pair. Longer pairs are truncated document-side first. |
| `SpecialTokens` | `Dictionary<string, int>` mapping special tokens to their IDs (e.g. `{ "[CLS]": 1, "[SEP]": 2, ... }`). Used by `SentencePiece` and `XlmRoberta` families that don't auto-add specials. Empty/null for `Bert` which manages specials internally. |

Default shipped registry (in `appsettings.json`):

```json
"Onnx": {
    "Enabled": false,
    "EmbeddingEnabled": false,
    "ActiveEmbeddingModel": "nomic-embed-text-v1.5",
    "ActiveRerankerModel": "mxbai-rerank-base-v1",
    "RerankBatchSize": 64,
    "GraphOptimizationLevel": "Basic",
    "EmbeddingModels": [
        {
            "Name": "nomic-embed-text-v1.5",
            "Description": "Default English embedding. 768-dim, 8192 token context. Strong MTEB performance, competitive with OpenAI text-embedding-3-small. Uses task prefixes (search_document: / search_query:). Apache-2.0, US supply chain.",
            "RepoId": "nomic-ai/nomic-embed-text-v1.5",
            "ModelFile": "onnx/model_fp16.onnx",
            "TokenizerFamily": "Bert",
            "VocabFile": "vocab.txt",
            "Dimensions": 768,
            "MaxSequenceLength": 512,
            "DocumentPrefix": "search_document: ",
            "QueryPrefix": "search_query: "
        }
    ],
    "RerankerModels": [
        {
            "Name": "mxbai-rerank-base-v1",
            "Description": "Default reranker. 184M-param DeBERTa-v2, ~46.9 NDCG@10 on BEIR subset. ~150ms batched on CPU for 50 candidates. Apache-2.0, English-focused. Best quality/latency tradeoff in the registry.",
            "RepoId": "mixedbread-ai/mxbai-rerank-base-v1",
            "ModelFile": "onnx/model_quantized.onnx",
            "TokenizerFamily": "SentencePiece",
            "SpmFile": "spm.model",
            "MaxSequenceLength": 512,
            "SpecialTokens": { "[CLS]": 1, "[SEP]": 2, "[PAD]": 0, "[UNK]": 3, "[MASK]": 128000 }
        },
        {
            "Name": "mxbai-rerank-large-v1",
            "Description": "Larger mxbai variant. 435M-param DeBERTa-v2, ~48.8 NDCG@10 (~+2 NDCG vs base). 642 MB quantized, ~300-500ms batched. Pick when search quality matters more than latency.",
            "RepoId": "mixedbread-ai/mxbai-rerank-large-v1",
            "ModelFile": "onnx/model_quantized.onnx",
            "TokenizerFamily": "SentencePiece",
            "SpmFile": "spm.model",
            "MaxSequenceLength": 512,
            "SpecialTokens": { "[CLS]": 1, "[SEP]": 2, "[PAD]": 0, "[UNK]": 3, "[MASK]": 128000 }
        },
        {
            "Name": "jina-reranker-v2-base-multilingual",
            "Description": "Multilingual reranker (89 languages). XLM-Roberta base, 278M params, 280 MB quantized. Pick when corpus contains non-English text. Quality competitive with mxbai-base for English, broader language support otherwise.",
            "RepoId": "jinaai/jina-reranker-v2-base-multilingual",
            "ModelFile": "onnx/model_quantized.onnx",
            "TokenizerFamily": "XlmRoberta",
            "MaxSequenceLength": 512,
            "SpecialTokens": { "<s>": 0, "</s>": 2, "<pad>": 1, "<unk>": 3, "<mask>": 250001 }
        }
    ]
}
```

**Default-by-order convention.** Whatever's first in `EmbeddingModels` is the active embedder. Whatever's first in `RerankerModels` is the active reranker when `RerankEnabled=true`. Switching defaults is one of:
- Reorder the array (move your preferred entry to the top)
- Comment out / delete the current first entry

This is intentionally simple and discoverable. The `Description` field is the docs explaining each option in place.

**Tokenizer families are code, model entries are config.** A user can add a new `SentencePiece`-family model to config without recompiling. Adding `Llama` or `Qwen` family rerankers (different tokenizer protocol) would require code. The plan ships with `Bert`, `SentencePiece`, and `XlmRoberta` support — that covers all three reranker registry defaults plus the embedding default.

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

## Phase 5 — Generalize Ollama Models to the Same Registry Pattern

Ollama is still in the picture for non-embedding tasks (symbol categorization in `reextract_library`, recon LLM in the CLI). The hardcoded model fields in `OllamaSettings` (`ClassificationModel`, `ReconModel`) get the same registry treatment as ONNX — config-driven list with first-entry-is-default, named `Description` per entry, name-based `Active` selector.

This phase also **deletes the legacy LLM/cross-encoder reranker code** (`OllamaReRanker`, `LlmQueryPlanner`, `QueryPlanScorer`, `ReRankerStrategy.Llm`, `ReRankerStrategy.CrossEncoder`, `OllamaSettings.ReRankingModel`, `OllamaSettings.CrossEncoderModel`). These routed to NoOp anyway per the existing comments in `OllamaSettings`; with `ONNX rerank shipped in Phase 3, they're dead weight.

---

### Task 12: Delete the legacy Ollama reranker and query planner

**Files:**
- Delete: `SaddleRAG.Ingestion/Embedding/OllamaReRanker.cs`
- Delete: `SaddleRAG.Ingestion/Embedding/LlmQueryPlanner.cs`
- Delete: `SaddleRAG.Ingestion/Embedding/QueryPlanScorer.cs`
- Delete related test files in `SaddleRAG.Tests/Embedding/`
- Modify: `SaddleRAG.Core/Enums/ReRankerStrategy.cs` — remove `Llm` and `CrossEncoder` values; keep `Off` and `Onnx` (rename `Onnx` → just keep `On` if you want a cleaner two-state enum, but `Off` / `Onnx` reads fine).
- Modify: `SaddleRAG.Ingestion/Embedding/ToggleableReRanker.cs` — drop the `Llm` and `CrossEncoder` dispatch branches.
- Modify: `SaddleRAG.Ingestion/Embedding/OllamaSettings.cs` — remove `ReRankingModel`, `CrossEncoderModel`, related defaults and constants.
- Modify: `SaddleRAG.Mcp/appsettings.json` — remove the dead `Ollama.ReRankingModel` / `Ollama.CrossEncoderModel` entries if present.

- [ ] **Step 1: Identify all references**

```
grep -rn "OllamaReRanker\|LlmQueryPlanner\|QueryPlanScorer\|ReRankerStrategy\.Llm\|ReRankerStrategy\.CrossEncoder\|CrossEncoderModel\|ReRankingModel" SaddleRAG.Mcp SaddleRAG.Ingestion SaddleRAG.Core SaddleRAG.Tests SaddleRAG.Database --include="*.cs"
```

- [ ] **Step 2: Delete the classes, remove enum values, drop DI registrations in `Program.cs`, delete tests.**

- [ ] **Step 3: Build + test**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
dotnet test SaddleRAG.Tests
```

- [ ] **Step 4: Commit**

Message: `chore(rerank): remove dead legacy Ollama reranker and query planner code`

---

### Task 13: Generalize remaining `OllamaSettings` model fields to the registry pattern

Same shape as ONNX: a list of named entries with `Description`, `Active{Task}Model` selector, first-in-list-is-default-if-unset.

**Files:**
- Modify: `SaddleRAG.Ingestion/Embedding/OllamaSettings.cs`
- Create: `SaddleRAG.Ingestion/Embedding/OllamaModelEntry.cs`
- Modify: `SaddleRAG.Ingestion/Embedding/OllamaBootstrapper.cs` (warmup pulls active model per task, with each entry's `WarmTimeoutSeconds` override)
- Modify: `SaddleRAG.Mcp/appsettings.json`
- Test: `SaddleRAG.Tests/Embedding/OllamaSettingsTests.cs`

New `OllamaSettings` shape:

| Property | Default | Purpose |
|---|---|---|
| `Endpoint` | `"http://localhost:11434"` | (unchanged) |
| `ActiveClassificationModel` | `"phi4-mini:3.8b"` | Name of `ClassificationModels` entry to use. Missing → first entry. Cannot be empty (classification is required for `reextract_library`). |
| `ActiveReconModel` | `"phi4:14b"` | Name of `ReconModels` entry. Missing → first entry. Cannot be empty (CLI recon depends on it). |
| `ClassificationModels` | (see below) | Ordered registry of classification models. |
| `ReconModels` | (see below) | Ordered registry of recon models. |
| `ModelPullTimeoutSeconds` | `600` | Default download timeout. Per-entry `WarmTimeoutSeconds` can override. |

`OllamaModelEntry`:

| Field | Purpose |
|---|---|
| `Name` | Ollama model tag (e.g. `"phi4-mini:3.8b"`). Used directly with the Ollama API. |
| `Description` | Human-readable rationale for why this model is offered for this task. |
| `WarmTimeoutSeconds` | Optional per-entry override of `WarmModelTimeoutSeconds`. |
| `MinVramGb` | Optional VRAM hint (informational, surfaced in logs/UI to help users pick). |

Default shipped registry (`appsettings.json`):

```json
"Ollama": {
    "Endpoint": "http://localhost:11434",
    "ActiveClassificationModel": "phi4-mini:3.8b",
    "ActiveReconModel": "phi4:14b",
    "ClassificationModels": [
        {
            "Name": "phi4-mini:3.8b",
            "Description": "Default classification model. Microsoft Phi-4 family, 3.8B params, fits ~4 GB VRAM. Strong instruction-following for category labeling in reextract_library. Western supply chain."
        },
        {
            "Name": "llama3.2:3b",
            "Description": "Alternative classifier. Meta Llama 3.2 family, 3B params. Slightly different instruction-following characteristics if Phi struggles on a specific corpus."
        }
    ],
    "ReconModels": [
        {
            "Name": "phi4:14b",
            "Description": "Default recon model used by the CLI fallback when no calling LLM is available. 14B params, needs ~12 GB VRAM. Does broader reasoning (language detection, casing conventions) than the classifier model."
        }
    ]
}
```

- [ ] **Step 1: Write `OllamaModelEntry` + update `OllamaSettings`**

Drop the old single-field properties (`ClassificationModel`, `ReconModel`); replace with the registry + active selectors.

- [ ] **Step 2: Update `OllamaBootstrapper`**

Bootstrap reads `ActiveClassificationModel` and `ActiveReconModel`, looks them up in their respective lists, pulls each on startup. Same warm/timeout flow as before but parameterized by the entry rather than the constants.

- [ ] **Step 3: Update call sites that read `OllamaSettings.ClassificationModel` / `OllamaSettings.ReconModel`**

Likely just a handful of services. Replace with lookup against the active entry: `settings.GetActiveClassificationModel()` → `OllamaModelEntry`.

- [ ] **Step 4: Update tests**

Existing tests that hardcoded `"phi4-mini:3.8b"` get switched to load from the configured registry.

- [ ] **Step 5: Build + test**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
dotnet test SaddleRAG.Tests
```

- [ ] **Step 6: Commit**

Message: `feat(ollama): generalize classification/recon models to registry pattern`

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
| Legacy Ollama reranker code (OllamaReRanker, LlmQueryPlanner, QueryPlanScorer) deleted | Phase 5, Task 12 |
| `ReRankerStrategy` enum reduced to `Off` / `Onnx` | Phase 5, Task 12 |
| Ollama `ClassificationModel` / `ReconModel` generalized to registry with Description per entry | Phase 5, Task 13 |
| First-in-list-is-default-if-unset convention applied uniformly to ONNX and Ollama registries | Phase 2 Task 4 + Phase 5 Task 13 |

## Key Decisions

1. **Embedding model: `nomic-ai/nomic-embed-text-v1.5`, fp16 variant.** (768-dim, Apache-2.0, 273 MB). **Confirmed working in Phase 1 spike.** Use the fp16 ONNX export (`onnx/model_fp16.onnx`); the fp32 export at 547 MB is unnecessarily big. ORT auto-converts fp16 outputs to float32, so no manual `Float16` handling is needed downstream. Fallback: `sentence-transformers/all-MiniLM-L6-v2` (384-dim, 90 MB, lower quality but ubiquitous).

2. **Reranker: default MxbaiBase. User-selectable from `{None, MxbaiBase, MxbaiLarge, JinaV2Base}`.** With batched inference (Phase 2 Task 6 requirement, see below), the latency cost of default-on rerank is ~150 ms per search — small enough to ship enabled.

   **Supported `OnnxSettings.RerankModel` values:**

   - **`MxbaiBase`** (default) — `mixedbread-ai/mxbai-rerank-base-v1`. DeBERTa-v2 base, 184M params, 244 MB quantized ONNX (`onnx/model_quantized.onnx`) / 738 MB fp32. **46.9 NDCG@10** on mxbai's 11-task BEIR subset, 72.3 Acc@3. Batched 50-doc rerank ~150 ms on CPU. Tokenizer: `SentencePieceTokenizer.Create()` against the model's `spm.model` (shipped at repo root).

   - **`MxbaiLarge`** — `mixedbread-ai/mxbai-rerank-large-v1`. DeBERTa-v2 large, 435M params, 642 MB quantized / 1.74 GB fp32. **48.8 NDCG@10**, 74.9 Acc@3. Same tokenizer code path as base (just bigger model file). Batched 50-doc rerank ~300–500 ms.

   - **`JinaV2Base`** — `jinaai/jina-reranker-v2-base-multilingual`. XLM-Roberta base, 278M params, 280 MB quantized ONNX (`onnx/model_quantized.onnx`) / 1.11 GB fp32. Multilingual (89 languages). Often quoted as quality-competitive or better than mxbai-base-v1 for English while adding non-English support. **Tokenizer caveat:** XLM-Roberta uses BPE-SentencePiece and the model repo does NOT ship a standalone `sentencepiece.bpe.model` — the SP model is embedded inside `tokenizer.json`. Wiring requires either (a) extracting the SP model from tokenizer.json once at install/build time and persisting it as `sentencepiece.bpe.model`, or (b) writing a small custom XLM-Roberta tokenizer wrapper. Phase 1 Task 2 Step 3 evaluates which approach is shorter.

   - **`None`** — no reranking. `ToggleableReRanker` returns candidates as-is. Use this if even ~150 ms is too much, or for diagnosing search-quality regressions to isolate the embedding side.

   **Why not mxbai-v2.** mxbai-rerank-v2 (Feb 2025, ~55 NDCG@10) is meaningfully better than v1 but ships PyTorch only — no ONNX. Out of scope for v1; could be a future option after we have an in-house ONNX conversion step.
   **Why not bge-reranker-v2-m3.** Only community-converted ONNX exists (`onnx-community/bge-reranker-v2-m3-ONNX`); no official ONNX. Tracked as a future add if multilingual demand grows beyond what jina-v2 covers.
   **Why not Alibaba models** (`gte-reranker-modernbert-base` etc.). Explicit non-goal per the strategy direction that triggered this pivot.
   **Why not ms-marco-MiniLM-L6-v2.** 2020-era baseline, significantly weaker than any of the three options above. Kept only as an *undocumented* emergency fallback if all three above are blocked.

3. **Tokenizer library: `Microsoft.ML.Tokenizers`** 2.0.0 (Microsoft-supported, native NuGet). Two tokenizer code paths required: `BertTokenizer.Create(vocabFilePath)` for the embedding model (nomic, BERT WordPiece), and `SentencePieceTokenizer.Create(spmModelStream, addBeginningOfSentence, addEndOfSentence, specialTokens)` for the reranker (mxbai, DeBERTa-v2 SentencePiece). Cross-encoder framing for BERT-family models uses the built-in `BertTokenizer.BuildInputsWithSpecialTokens` + `CreateTokenTypeIdsFromSequences` helpers; for SentencePiece models, the framing is built manually using the model's known `[CLS]`/`[SEP]`/`[PAD]` ids from `tokenizer_config.json`.

4. **Required `SessionOptions`: `GraphOptimizationLevel = ORT_ENABLE_BASIC`.** The default `ORT_ENABLE_ALL` triggers a `SimplifiedLayerNormFusion` bug in ORT 1.26 against precision-cast nodes inserted by both ONNX exports. Symptom: `OnnxRuntimeException: ... GetIndexFromName itr != node_args.end() was false. Attempting to get index by a name which does not exist: InsertedPrecisionFreeCast_/emb_ln/Constant_output_0`. Dropping to `BASIC` skips the problematic fusions; basic optimizations (constant folding, redundant-node elimination) still run.

5. **Model distribution: download at install (Option B from Task 3).** MSI stays small; existing `--prewarm` mechanism handles the download. Acceptable network dependency for our deployment context. Re-evaluate if customer environments turn out to be air-gapped.

5. **In-process inference, not subprocess.** This is the entire rationale for ONNX over TEI. No supervisor, no port, no IPC. ONNX Runtime is loaded into the SaddleRAG.Mcp process directly.

6. **Why not keep Ollama?** Ollama is a separate process customers must install and run alongside SaddleRAG. The whole migration goal is to eliminate that external dependency and ship inference as part of SaddleRAG itself.

7. **GPU support deferred.** `Microsoft.ML.OnnxRuntime.Gpu` exists and slots in via a different NuGet package, but adds CUDA + cuDNN dependencies and only matters for high-throughput scenarios. Out of scope for v1; flip a NuGet ref later if needed.

8. **Why preserve the TEI branch.** Branch `feat/tei-migration-revised` retains the TEI submodule plus the Windows build recipe we discovered. If ONNX has unexpected blockers in Phase 1, that branch is the documented fallback path. Delete the branch only after Phase 4 is verified working.
