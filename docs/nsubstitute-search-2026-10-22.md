# Remaining NSubstitute search and repeated answers

Issue: <https://github.com/JackalopeTechnologies/SaddleRAG/issues/189>

The question “NSubstitute: return a constant without caring about any of the arguments.” missed useful instructions in the first five answers with DirectML enabled and disabled. Search could also return identical text twice when alternate addresses for the same page had different category labels from import.

## Changes

Ordinary connecting words now contribute half their previous keyword weight. A word such as “about” could otherwise outweigh words describing the requested operation in a small documentation collection. All query terms still participate. Quoted or backticked words, callable names such as `About()`, and questions consisting entirely of connecting words retain full weight. Technical words, negation, and quantifiers retain full weight. Google still receives the complete original question.

Search also treats identical passages in the same technical context as one answer even when their import categories differ. It retains the highest-ranked copy and its citation. Library, version, title, section, symbol, code language, document source, and complete text still distinguish answers. Both sorter modes apply this rule before limiting the number of returned results. This does not remove duplicate candidates before model evaluation.

Neither change requires re-embedding, a keyword-index rebuild, or a database migration.

## Comparison

The comparison calls the actual `SearchTools.SearchDocs` method in an isolated process, using the installed local document vectors and keyword indexes, Google `embeddinggemma-2:270m`, and `mxbai-rerank-base-v1` on DirectML. The sorter uses the installed batch size of 64 and four runtime threads. Each call chooses its own sorter mode; the running service is not reconfigured.

The same 43 questions cover NSubstitute, SciChart, Math.NET, and Toshiba documents, including the earlier holdouts and wording variants. Three warmed rounds alternate mode order: 129 searches per mode, 258 total. All timings are retained. These are local warmed search measurements, not first-request or end-to-end client latency guarantees.

| Check | Sorter disabled | DirectML enabled |
|---|---:|---:|
| Useful answer within five, each round | 43/43 | 43/43 |
| Useful answer first, each round | 22/43 | 24/43 |
| Mean time across all 129 requests | 323 ms | 754 ms |
| Repeated identical answers in the same context | 0 | 0 |
| Remaining NSubstitute question: answer position | 2, 2, 2 | 3, 3, 3 |
| Remaining NSubstitute question: mean time | 28 ms | 452 ms |
| Earlier NSubstitute “ignore every argument” question: answer position | 5, 5, 5 | 1, 1, 1 |
| Earlier NSubstitute “ignore every argument” question: mean time | 19 ms | 415 ms |
| NSubstitute “ignore all parameters” variant: answer position | 4, 4, 4 | 1, 1, 1 |
| SciChart vertical hit-test value: answer position | 3, 3, 3 | 3, 3, 3 |
| Original VF-S9 timing question: answer position | 1, 1, 1 | 1, 1, 1 |
| Original VF-S9 timing question: mean time | 146 ms | 521 ms |

Useful-answer checks retain the earlier criteria: the expected stored passage, byte-identical text with equivalent source context, or a qualifying explanation in the returned body. Navigation labels alone do not count. The newly recovered NSubstitute answer explains `Arg.Any<T>()` and demonstrates a fixed-value `.Returns(7)` call. It provides usable instructions; it is not the dedicated `ReturnsForAnyArgs` shortcut page. Exact stored-passage and reviewed answer positions are recorded separately in the evidence.

DirectML improves first-place placement for two more questions overall but adds about 431 ms to the mean search time and does not increase first-five coverage in this set. The evidence supports considering the faster mode after deployment, while preserving the operator's decision about the live sorter setting. This finite set does not establish equal quality for every future question.

## Verification

The category-difference duplicate tests failed before the repair and passed afterward. Regression tests cover both sorter modes, preservation of distinct technical contexts, reduced connecting-word influence without discarding matches, and preservation of literal or standalone terms.

The clean Release rebuild of `SaddleRAG.slnx` completed with zero warnings and errors. The full test run completed with 2,510 total tests: 2,486 passed, 24 skipped, zero failed, and zero errors. Skips cover optional model fixtures, alternate runtime builds, symbolic-link privileges, and explicit diagnostics. The search comparison separately exercised the selected Google embedding model and the DirectML sorter against the stored libraries.

An initial experiment removing connecting words entirely improved the reported wording but lost two other answers. It was rejected. The final weighting change retains those answers in all three rounds. No experimental change was installed.

## Installed state

After verification, the running service still reports `1.13.0+dbd8d3e5f7830fc9cc161d7128800cc5b01e4328`, Healthy, with the `Onnx`/DirectML sorter enabled. MongoDB, Ollama, and document conversion report Healthy, with no unread warnings or errors. These source fixes have not been installed, and no live sorter setting or stored content was changed.

Evidence: [comparison results, all timings, and first-round responses](nsubstitute-search-2026-10-22.json). The source revision tested is `321337d7f750e6979b1b346b33e215446e170c5a`. Local comparison tools, detailed traces, and build/test output are retained under `E:\CodexScratch\nsubstitute-search-20261022`.
