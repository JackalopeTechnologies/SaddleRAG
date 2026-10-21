# Google EmbeddingGemma 2 versus the current Nomic setup

Issue: [#189](https://github.com/JackalopeTechnologies/SaddleRAG/issues/189).

Google `embeddinggemma-2:270m` is the preferred candidate for the next embedding
migration based on this local comparison. No live provider, model, settings,
library vectors, or installed binaries were changed. Qwen `qwen3:1.7b` was removed
from the existing shared Ollama store; the Google candidate was added there.

## Results

The same 667 stored passages and 29 questions were used for all three paths.
Questions and known answering passages were fixed before model execution. A
match means retrieval of that known passage, accepting exact duplicate content.
These are embedding-only results, without keyword scoring or the reranker.

| Measurement | Current Nomic / DirectML | Nomic / corrected Ollama | Google / corrected Ollama |
| --- | ---: | ---: | ---: |
| Known answer ranked first | 19/29 (65.5%) | 22/29 (75.9%) | 26/29 (89.7%) |
| Known answer in first five | 26/29 (89.7%) | 28/29 (96.6%) | 29/29 (100%) |
| Average reciprocal rank | 0.772 | 0.847 | 0.934 |
| Warm single-query median | 86.1 ms | 18.8 ms | 18.1 ms |
| Warm single-query 95th percentile | 137.1 ms | 22.7 ms | 22.7 ms |
| Document throughput, two passes | 11.1 / 8.3 passages/s | 41.2 / 100.3 passages/s | 69.5 / 69.9 passages/s |
| Initial model load plus first query | 1.45 s | 23.40 s | 31.83 s |
| Sampled GPU increase over baseline | 511 MiB | 732 MiB | 786 MiB |

Google improved the difficult chart coordinate and visual-child questions. It
was not better on every question: the known conversation-history passage ranked
fourth with Google and second with both Nomic paths. Nomic through Ollama had the
highest second-pass document throughput; Google had more consistent throughput
across the two passes.

Loading deserves separate attention. Two later requests after automatic idle
unloading took 7.94 / 3.38 seconds for Nomic and 4.95 / 4.70 seconds for Google
as measured by the PowerShell client. Ollama reported model-loading time of
1.29 / 1.27 seconds for Nomic and 2.86 / 2.60 seconds for Google. These client
measurements use a different HTTP client from the main comparison and should
not be treated as directly comparable to its warm query timings. Both models
automatically unloaded after the explicit two-second retention used in that
check. The implemented production default is 60 seconds, configurable from zero
to 300 seconds, only for embeddings.

## Method and limits

- Windows, NVIDIA GeForce RTX 4090 with 24,564 MiB reported memory; Ollama 0.40.7.
- Actual SaddleRAG provider code at commit `0c8d592`, .NET 10.0.13, debug build;
  all three paths used the same runner build. DirectML activation was verified.
- Native Nomic used the installed `nomic-embed-text-v1.5` model, basic graph
  optimization, its existing 512-token passage limit, and four CPU threads.
  The installed service uses automatic thread selection; it was not changed.
- Ollama used `nomic-embed-text:latest` (137M, F16) and
  `embeddinggemma-2:270m` (271M, Q8_0), 768 output dimensions, four CPU threads,
  batches of 16, and model-specific document/query instructions.
- One model ran at a time. Each run included one initial query, two complete
  document passes, and three rounds of all 29 individual queries. Vectors were
  checked for correct dimensions, finite values and nonzero magnitude.
- Seven local libraries: NSubstitute, OllamaSharp, Math.NET Numerics, Math.NET
  Filtering, Toshiba manuals, Advanced Installer, and SciChart WPF. Up to 120
  passages per library were selected by stable identifier hash, among passages
  containing 120–1,600 characters. Raw copied passages remain local.
- Only three passages exceeded the native tokenizer's 512-token limit; the
  longest had 914 tokens. Removing those candidates left all three paths'
  first-place and first-five counts unchanged. All known answering passages
  fit the native limit. This does not test long-document retrieval.
- This is a small, manually authored known-answer set. Labels are not exhaustive
  judgments of every potentially relevant passage. It is useful evidence for
  choosing a candidate, not proof of performance on all libraries or of final
  search quality after keyword scoring and reranking.
- GPU figures are whole-machine peak-minus-baseline samples at roughly half-second
  intervals. The desktop and installed service remained running, so these are
  approximate incremental measurements, not isolated per-process allocations.
  Ollama reported resident model weights of about 262 MiB for Nomic and 290 MiB
  for Google; the peak figures also include runtime overhead and other activity.
- Runtime emitted its usual notice about some native model nodes being assigned
  outside the preferred provider, including CPU shape operations. DirectML was
  active; this notice is not evidence that the model fell back entirely to CPU.

The installed Ollama Nomic artifact reports a 2,048-token context; the tested
Google runner reports 32,768. Google's model card specifies an 8,192-token
maximum. This short-passage comparison does not establish support beyond the
model publisher's documented limit. The two Nomic formats are not asserted to
be numerically identical. [Google model card](https://huggingface.co/google/embeddinggemma-2),
[Ollama Google model](https://ollama.com/library/embeddinggemma-2:270m),
[Nomic model card](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5).

## Integration and migration boundary

The corrected Ollama provider sends ordered batches, supports Nomic and Google
task instructions, validates response count/dimensions/values, rejects silent
input truncation, and stops on cancellation. It retries transient transport
failures. Embedding retention is bounded independently of Devstral and recon.
See [configuration and migration notes](ollama-embeddings.md).

Task instructions are explicitly enabled during the comparison. The default
remains off for compatibility with older Ollama-created vectors. Enabling them
against an existing index requires regenerating that index; the setting is not
currently persisted in library metadata. A future approved migration must keep
the configuration and stored vectors together and validate search before use.

Replacing embeddings alone would still leave the existing DirectML reranker in
use. Keep `Onnx.Enabled=true` and change only `Onnx.EmbeddingEnabled` when that
migration is approved. No such setting change or re-embedding was performed.

## Verification and evidence

- Focused Ollama checks: 87 passed.
- Clean release rebuild of the solution: zero warnings and zero errors.
- Full suite: 2,454 total, 2,430 passed, 24 skipped, zero failures. Skips cover
  optional model fixtures, symbolic-link prerequisites, opt-in conversion and
  classifier diagnostics, and CPU-only checks in the DirectML build. The actual
  installed native embedding model was exercised by the separate comparison.
- Final installed service health: Healthy. Final Ollama running-model list: empty.
- Machine and user `OLLAMA_KEEP_ALIVE` remain `0`.
- Installed settings hashes before and after match exactly:
  `appsettings.json`: `8246C9831DFC4B8303C25DBA796F20E267CA99A368E5ECB7F6A69F8CA99C0939`;
  `runtime-overrides.json`: `6A06FDE615E62534AAAE754E1FD4D71057F83C0AC11E78C2D4F080E3E17580AF`.

Local evidence folder: `E:\CodexScratch\embedding-comparison-20261021`.
It contains the read-only export script, fixed questions, corpus, actual-provider
runner source/project, per-model results, memory samples, idle-reload script and
results, runtime state, verification script and full test report. Raw document
text was not uploaded. Aggregate results are also saved in
[embedding-comparison-2026-10-21.json](embedding-comparison-2026-10-21.json).

To repeat the main runs after building `Harness\EmbeddingComparison.csproj`
with `dotnet build -m:2`, run its executable once each with `directml`, `nomic`,
and `gemma`, sequentially. The local runner names the installed settings and
worktree explicitly; it never writes to MongoDB or installed configuration.
The idle check is `Test-Idle-Reload.ps1`; the solution verification is
`Verify-Embedding-Change.ps1`. These local runner artifacts have not been pushed.

Evidence identities:

- Corpus SHA-256: `0F3B551245C5DCBEE4B1C967CB96F0CD1EC21073941DEABF50A400120880CF5B`.
- Questions SHA-256: `60AB3E5A73C587B299C420478903EAB73F7B228B124B580C2ACF464AE8A868DB`.
- Native model SHA-256: `CF5B5A86EDB00F895561803CFC04729090A958340B8CA2AD76C143F565F6BB04`.
- Ollama Nomic digest: `0a109f422b47e3a30ba2b10eca18548e944e8a23073ee3f3e947efcf3c45e59f`.
- Ollama Google digest: `c21dd30c1e38444d3b4faa091407b6fa97221447b4965d74931d43d7f09bbbfd`.
- Libraries: OllamaSharp 5.4.30, Microsoft.ML.OnnxRuntime.DirectML 1.24.4,
  Microsoft.ML.Tokenizers 2.0.0.
