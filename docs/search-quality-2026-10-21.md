# Search ranking and catalog repair — 2026-10-21

Tracked in [issue 189](https://github.com/JackalopeTechnologies/SaddleRAG/issues/189).

Installed build: `1.12.3-local.search.20261021+e6f2662f783284f74231f588cf04e37bb862b65e`.
Google `embeddinggemma-2:270m` remains the embedding model. The DirectML
`mxbai-rerank-base-v1` sorter remains enabled, with its influence bounded by the
retrieval ordering. No model replacement or re-embedding was needed for this repair.

## Cause and correction

The NSubstitute return-for-any-arguments pages were strong keyword candidates,
but the sorter demoted them below the five returned answers. For SciChart, the
short `HitTestInfo.YValue` page is only 558th by vector similarity, but the complete
`HitTestInfo` properties page is already the first vector candidate and explicitly
documents `YValue`. Keyword score blending demoted that useful candidate before
the authoritative sorter demoted it further. Increasing the vector candidate pool
to 1,000 did not repair either result.

Search now combines rank positions instead of directly mixing provider score
scales. Within eligible vector candidates, the retrieval score is
`(1 - Bm25Weight) / vectorRank + Bm25Weight / keywordRank`; a missing keyword match
contributes zero. Existing inferred-subject boosts participate in the semantic
ordering. Equal score keys share a rank. The optional sorter then contributes
an equal vote alongside the retrieval rank: `0.5 / retrievalRank + 0.5 / sorterRank`.
An unscored candidate receives only the retrieval contribution. Zero-score symbol
injections receive no retrieval vote, while still entering the bounded sorter
window. Provider scores remain separate diagnostic fields; the combined score is
an ordering score, not an answer probability.

The sorter-off path also now orders by retrieval score before selecting results.
Previously, prepended symbol candidates with zero scores could occupy all five
results, contrary to the documented intention that they enter only the sorter
window. Nine regression tests cover independent leaders, tied scores, irrelevant
keyword matches, subjects, missing sorter rows, candidate limits, and disabled
sorting. Existing published-version and explicit-subject tests continue to pass.

## Installed comparison

The same 29 saved questions were run through the installed server before and after
the change, with sorting enabled and disabled. Each mode received a warmup query.
The original exact-passage criterion is retained separately from manual assessment
of equivalent answers. [Per-question results](search-quality-2026-10-21.json)
include queries, result URLs, provider scores, and review notes.

| Installed search | Exact passage first / 29 | Exact passage within five / 29 | Reviewed answer within five / 29 | Mean search time |
| --- | ---: | ---: | ---: | ---: |
| Before, DirectML sorter on | 13 | 23 | 27 | 831.72 ms |
| Before, sorter off | 12 | 24 | Not fully reviewed | 387.14 ms |
| After, DirectML sorter on | 15 | 24 | 29 | 815.07 ms |
| After, sorter off | 14 | 25 | 28 | 356.86 ms |

These timings are one local warmed pass, not a statistically controlled performance
claim. The full-library test is distinct from the earlier isolated 667-passage
embedding benchmark.

With the installed sorter enabled, the NSubstitute answer is third and the SciChart
properties answer second. Both positions were also confirmed through Codex's
connected SaddleRAG search tool. The exact short SciChart property page remains
outside the candidate pool; the returned properties page supplies the requested answer.

Five after-change questions use equivalent pages or passages rather than the
fixture's exact stored passage: NSubstitute return-for-any-arguments and constructor
behavior, SciChart `YValue` and `FindVisualChild<T>`, and Toshiba frequency/detection
band parameters. The constructor result includes the explanation of base-constructor
execution and recommends interfaces. These are reviewed content matches, not exact
identifier matches.

Six additional phrasings of the two problem questions were checked after the change.
With sorting on, all six returned usable answer material within five results. Four
contained the specifically checked method/property; two NSubstitute questions
returned the documented `Arg.Any<T>()` alternative rather than `ReturnsForAnyArgs()`.
With sorting off, five of six supplied usable material. The raw automatic shortcut
check was four of six in both modes; the broader assessment is documented separately.

Keep DirectML sorting enabled: the repaired combination retains its benefit on the
VF-S9 output-phase-failure timing question and one new NSubstitute phrasing. Disabling
it is faster, but loses those answers in this comparison. This is a bounded local
sample, not a guarantee for all questions or document collections.

## Missing library records

The two missing-parent groups contained duplicates of current published libraries:

| Stranded name | Retained library | Removed passages | Removed source pages | Retained passages | Retained source pages |
| --- | --- | ---: | ---: | ---: | ---: |
| `h-notifyicon-wpf` | `h-notifyicon` | 32 | 57 | 462 | 209 |
| `automation1.net` | `automation1-dotnet` | 511 | 16 | 2,540 | 910 |

All 73 source pages matched the retained URL and raw text exactly. Of 543 passages,
538 matched content and URL exactly; the other five were fully covered by retained
page passages after whitespace normalization and chunk-boundary alignment. No unique
documentation needed moving, and no empty duplicate catalog entries were created.

The existing `cleanup_orphans` preview found exactly those two names. Cleanup job
`dcb158e0-7798-46cc-8aa8-a174cb57d52b` completed, removing the duplicates plus one obsolete
keyword index and 64 index shards. A subsequent global preview reports zero orphaned
pairs. Both retained version summaries were corrected to their actual stored counts.
The database contains 34 libraries and 146,679 passages after repair.

Before/after SHA-256 checks confirm the retained pages and complete passage records,
including embeddings, are unchanged:

| Retained library | Pages SHA-256 | Passages SHA-256 |
| --- | --- | --- |
| `h-notifyicon` | `ddf10b77c91789b1d4fb601ee481d217afae9741faf5cf212d616c0bdcfafb9c` | `0366d1b4d71fd1f11194359282a7920604115c5863c2dd4e942bf79885cba68b` |
| `automation1-dotnet` | `d505d8fd0bcbb1d3f4f27af3bf669f5a8bca738c2224fdffd8934b8f13dc7f6b` | `9380b7465fa369e6bbfa5bc3eaebecc15e5b075783932403196c82a08237ed43` |

Only the affected records were exported for recovery; no database clone was made.
The old names' historical origin is unproven. The repair relies on current coverage
checks, not an assumption about how the records became stranded.

## Validation and installation

- Clean solution rebuild: zero warnings and errors.
- Full suite: 2,445 passed, 24 skipped, zero failures. Skips concern optional native
  model fixtures, symbolic-link privileges, alternate processor builds, and explicitly
  enabled diagnostics. Installed model execution was exercised separately above.
- Combined server, tray, command-line and installer-helper package: 538 manifest
  files verified; 536 installed non-configuration files verified; 23 files replaced.
- Installed configuration hashes unchanged. Google embeddings, local-only MongoDB,
  and bounded Ollama retention remain configured as before.
- Service running with automatic startup and completed warmup; MongoDB, Ollama,
  and document conversion healthy. Installed tray running and connected to port 6100.
- Sorter setting restored to `Onnx` after the comparison; both requested searches
  confirmed using the connected Codex tools.

Local full-response evidence, comparison programs, targeted recovery export, package,
installation report and build/test logs are under
`E:\CodexScratch\search-quality-20261021`. The preceding migration's 147,222-passage
audit remains a valid historical snapshot before duplicate consolidation.
