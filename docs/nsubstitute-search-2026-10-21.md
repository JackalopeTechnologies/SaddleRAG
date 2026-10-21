# NSubstitute retrieval without the DirectML sorter

Issue: <https://github.com/JackalopeTechnologies/SaddleRAG/issues/189>

The reported question was: “How do I make NSubstitute ignore every argument when returning a fixed value?” The relevant argument-matcher explanation was already semantic candidate 5 and keyword candidate 4. Combining those rankings put it sixth. Two byte-identical copies of “How NSubstitute works”, served at the ordinary URL and its `/index` alias, occupied result positions 4 and 5.

## Change

Search now selects distinct passages after ordering and before applying the requested result limit. The highest-ranked copy keeps its original text, citation, and scores. Equality includes library, version, category, title, section, symbol, code language, document provenance, and complete passage text. Different technical contexts and non-identical text remain separate. Both sorter modes use the same selection rule.

This changes neither retrieval weights nor the models. It requires no data migration, keyword rebuild, or new embeddings. The DirectML candidate window remains unchanged; this fix removes duplicate returned passages, not duplicate model evaluations.

## Comparison method

The comparison calls the actual `SearchTools.SearchDocs` method with Google `embeddinggemma-2:270m`, stored document vectors and keyword indexes, and `mxbai-rerank-base-v1` on DirectML. It uses the installed sorter batch size of 64 and four runtime threads. Per-call settings in an isolated process compare Off and Onnx without toggling the running service.

Coverage includes the 29 original questions, six prior holdouts, four VF-S9 variants, and four additional NSubstitute phrasings. The final comparison has three warmed rounds with alternating mode order: 258 searches. All measured requests are retained, including latency outliers. First-round full responses and every timing are in the accompanying JSON.

Exact stored IDs and useful answer positions are reported separately. The expected Math.NET named-matrix answer now comes from an alternate URL with byte-identical text in the same library, title, and section; it remains a valid first-place answer. Existing equivalent-answer checks remain in effect. Navigation labels do not count as explanations.

## Results

| Check | Sorter off | DirectML sorter |
|---|---:|---:|
| Reported NSubstitute question, answer position | 5, 5, 5 | 1, 1, 1 |
| Reported NSubstitute question, mean time | 20 ms | 415 ms |
| All 39 earlier questions, useful answer within five | 39/39 each round | 39/39 each round |
| Expanded 43 questions, useful answer within five | 42/43 each round | 42/43 each round |
| Expanded set, mean time across 129 requests per mode | 335 ms | 743 ms |
| Original 29 questions, mean time | 297 ms | 698 ms |
| Original VF-S9 timing question, answer position | 1, 1, 1 | 1, 1, 1 |
| Original VF-S9 timing question, mean time | 135 ms | 496 ms |

The reported NSubstitute timings were 23/19/19 ms without DirectML and 423/415/407 ms with DirectML. The fix closes the only sorter-off miss in the earlier 39-question comparison. DirectML still puts that explanation earlier in the response, but provides no additional first-five coverage in this expanded check. These are warmed local measurements, not a guarantee for first requests or untested questions.

## Additional coverage limit

The new question “NSubstitute: return a constant without caring about any of the arguments.” does not retrieve the direct explanation within five in either mode. The intended `ReturnsForAnyArgs` page is semantic candidate 5; keyword evidence promotes other pages, and DirectML does not recover the direct answer. The returned getting-started page contains a calculated-return example using `Arg.Any` for both inputs, but that is not counted as a direct fixed-value answer.

This is retained as a shared quality gap under issue 189, which remains open after this release. It does not invalidate the reported duplicate-crowding repair. A further controlled investigation should compare extraction of the documentation body and keyword ordering independently. Repeated navigation is present in stored passages; its causal effect on this wording has not been proven. No speculative text cleanup or ranking adjustment was installed.

## Verification and installation

The clean solution rebuild completed with zero warnings and errors. The full suite passed 2,467 tests, skipped 24, and had zero failures. Skips cover optional native-model fixtures, alternate runtime builds, symbolic-link privileges, and explicit diagnostics. Four new test cases cover distinct results and preservation of technical context in both sorter modes. The comparison separately exercises the installed Google model and DirectML sorter.

Installed version: `1.12.5-local.search.20261021+d02d363793faa1c74b00866fd2ef39d32023a428`. The combined server/tray/command-line/helper package verified 538 files; installation verified 536 application files, changed 23, and preserved both existing configuration files. The service is Automatic and Healthy with startup complete. The installed tray restarted and connected to port 6100.

The live sorter remains `Onnx`/DirectML. Google remains the embedding model. No database content, embeddings, model installation, or live ranking setting was changed. The first installed NSubstitute request after restart took 3,410 ms, including 2,722 ms for embedding and 596 ms for sorting. That first-request delay is separate from the warmed comparison and has not been causally isolated.

Evidence: [nsubstitute-search-2026-10-21.json](nsubstitute-search-2026-10-21.json). The local comparison source, review script, test output, combined package, and installation reports are under `E:\CodexScratch\nsubstitute-search-20261021`.
