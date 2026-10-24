# MCP and dependency update — 2026-10-24

Source checkout: `E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills`, branch `codex/mcp-spec-skills`. The final executable-source revision verified below is `8fe4111`; subsequent report/ledger cleanup does not change executable inputs. The primary checkout and installed service were not replaced.

## Verified baseline and sources

The starting revision is `8ebcdf81bc3d07a3f2c62059de8809d9d0db4c3b`. Its normal Release solution build passed with zero warnings and errors. It already referenced `ModelContextProtocol.AspNetCore` 2.3.0; this change does not claim to have upgraded that package.

Official sources checked on 2026-10-24:

- [MCP specification 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28), the destination of the official latest-specification link.
- [Official C# SDK release 2.3.0](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.3.0).
- [Skills extension](https://skills.extensions.modelcontextprotocol.io/specification/stable/skills).
- [Streamable HTTP transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).
- SDK 2.3.0 source for [custom request registration](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/src/ModelContextProtocol.Core/Server/McpServerOptions.cs) and [request handler shape](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/src/ModelContextProtocol.Core/Server/McpServerRequestHandler.cs).

## Server behavior

The server retains the SDK's protocol negotiation and stateless HTTP configuration. It does not pin a modern-only protocol version. Existing tools, prompts, installed skill files, and client configuration writers retain their interfaces.

The server now advertises `io.modelcontextprotocol/skills` and serves `skills/list` and `skills/get` for MCP 2026-07-28 requests. All eight packaged skills come from the existing embedded catalog. Each manifest contains the original YAML fields, a complete file list, SHA-256 digests, and byte lengths. The scrape strategy's referenced case study is included. The resource URIs are `skill://<name>/SKILL.md`, with supporting files beside that document. All nine files are available through standard `resources/list` and `resources/read`, including to older clients. Existing prompts continue returning the guide body without frontmatter.

Only exact, known embedded URIs are accepted. No request can resolve a filesystem path or fetch another URL. Unknown and malformed resource/skill URIs produce invalid-parameter errors. The small catalog is returned in one page; no continuation cursor is issued, and unexpected cursors are rejected. No directory-reading or resource-subscription capability is advertised.

Custom skills responses carry the modern complete-result and cache fields. Shared core responses leave revision-dependent serialization to the SDK, so older clients do not receive modern-only fields. The skills gate uses the transport's validated protocol context; metadata inserted into a legacy request cannot enable it.

SDK 2.3.0 has no typed Skills registration API. The only available extension registration API is marked experimental. A narrowly scoped, commented `MCPEXP002` suppression surrounds those two registrations; no project-wide warning policy is relaxed. A future SDK update must recheck this integration.

## Security and compatibility

The MCP route rejects untrusted browser `Origin` headers with HTTP 403 before dispatch. By default, requests without that header are allowed, and browser requests must match the requested loopback origin. A matching hostile Host/Origin pair is insufficient. Operators can explicitly list trusted origins in `Mcp:AllowedOrigins`; entries must be HTTP or HTTPS origins without credentials, paths, queries, or fragments. This does not enable cross-origin browser access by itself.

Origin validation is not authentication. The default endpoint remains local. Remote exposure still requires deployment-specific authentication, transport encryption, trusted proxy configuration, and host restrictions. This work does not establish that a remote deployment is secure or configure one.

Reading a resource does not activate a skill or authorize its actions. Host applications control skill approval and verification. Digests detect inconsistent content; they are not signatures or independent proof of trust. Current client support for the extension varies; the prompt fallback remains available.

Optional protocol features such as resource subscriptions, directory reads, task-backed execution, sampling, and elicitation are not newly advertised. Existing SaddleRAG background jobs remain its existing tool interface. No speculative extension or draft protocol is claimed.

## Ingestion repairs found during this work

The documented reconnaissance sequence saved a library profile before the first scrape. The ownership check then treated that configuration as orphaned acquired content and rejected the scrape. Database evidence now distinguishes the profile from pages/chunks/indexes. First web publication may use the profile, while directory registration, cleanup, and package import retain their ownership protections. Tests cover profile-only data and actual orphaned content separately.

Cancellation was also dropped between the classification stage and its classifier/page processor, and cancellation could be caught as an ordinary classification error. The operation token now reaches both stage paths, single-page ingestion, and the command-line ingestion path. Classification cancellation propagates rather than being counted as a failed page. This has no generation-limit or model-selection change.

An independent classifier timeout still follows the per-page failure path. Only cancellation requested by the operation token stops the pipeline; otherwise the upstream crawler could be left waiting on an abandoned bounded queue. Regression tests distinguish these cases for both stage paths and the native classifier wrapper.

Corpus checks also exposed navigation controls that change the displayed article when expanded. Capturing a title before clicking them and a body afterward can associate one page's title with another page's content. The crawler repair preserves the original article and links before navigation expansion and avoids known navigation/popup controls. A local browser regression reproduces a control that navigates. The first affected protocol-corpus attempt was rejected during validation rather than accepted on its successful job status alone.

Repeated sidebars and extension aliases could queue the same page many times before it was visited. Canonical reservation now prevents repeated admission while preserving requested fetch URLs, explicit retries, rendering escalation, and resume behavior. URL and depth eligibility are checked before reservation, so a rejected route cannot suppress an eligible one. Repeated rejected targets produce one audit event per normalized target rather than one per repeated navigation edge. Audit saturation now fails the affected crawl and propagates to its caller; it no longer reports successful completion or diagnoses an unproven database outage. The bounded buffer and periodic flush design are retained. See [crawl admission and audit capacity](crawl-queue-audit-2026-10-24.md) for semantics and limits.

Full ingestion deliberately refuses to overwrite Published versions. Forced scraping, rescraping, and URL correction previously performed preliminary mutations before reaching that refusal. Their entry points now refuse published targets before queueing or deleting data. URL correction also uses the existing per-library ownership lease around deletion and rechecks publication while holding it, preventing a concurrent full scrape from publishing between the check and deletion. Busy or conflicting ownership stops correction before mutation. Tool descriptions and packaged guides explain that force retries unpublished partial attempts; it does not bypass publication protection. Same-version full replacement is not introduced. The separate `add_page` interface retains its existing ability to extend a version.

Single-page ingestion now refreshes both persisted text-search and live vector indexes over the complete stored chunk set, then updates the existing version's page/chunk counts. It preserves other metadata and the current-version selection. If index refresh fails, it does not publish successful summary counts; already persisted page/chunk writes are not rolled back. Empty results update counts without an unnecessary index rebuild. This repairs the stale inventory and missing live search results observed while adding the SDK release notes.

`add_page` now accepts optional `contentSelector`. An explicit selector must match nonempty article text; invalid, missing, or empty matches return the existing Failed result with a clear reason before page/chunk persistence. Explicit selection does not substitute another frame or a broader default region. Existing calls and full-scrape extraction keep their defaults. This gives callers a supported way to exclude navigation from a single-page import; it does not claim automatic extraction is correct for every site or that the reported release-note category was a model defect.

Language diagnostics distinguish missing code-language metadata from positively observed wrong-language code. The existing 30% threshold is applied within tagged code, with the documented C# spellings treated consistently. Untagged prose remains visible as unknown; it no longer supplies false mismatch evidence. Existing saved suspect markers are not migrated automatically. Source code-language extraction remains a separate limitation, described in [language mismatch evidence](language-diagnostics-2026-10-24.md).

When web-ingestion ownership is unavailable, the diagnostic now reads the selected profile's recorded ownership and distinguishes directory mode, a pending rename, a web operation's recorded expiry, and an unknown/raced state. It does not steal a lease, shorten its duration, or retry acquisition automatically. Controlled-time tests retain active-owner exclusion and allow safe recovery after expiry for both source modes.

## NuGet changes

Versions were compared against the configured feeds and official NuGet metadata. Fifteen existing central pins changed; YAML parsing adds one dependency. The SDK and bundled .NET runtime remain 2.3.0 and 10.0.13 respectively.

| Package | Before | After |
| --- | --- | --- |
| AngleSharp | 1.7.0 | 1.8.5 |
| coverlet.collector | 10.0.1 | 10.1.0 |
| Microsoft.ML.OnnxRuntime / .Gpu | 1.28.0 | 1.31.0 |
| Microsoft.ML.OnnxRuntimeGenAI / .Cuda | 0.15.1 | 0.18.0 |
| Microsoft.ML.Tokenizers | 2.0.0 | 2.0.1 |
| Microsoft.NET.Test.Sdk | 18.8.1 | 18.11.0 |
| Microsoft.Playwright | 1.61.0 | 1.64.1 |
| MongoDB.Driver | 3.12.0 | 3.13.0 |
| NSubstitute | 6.0.0 | 6.2.0 |
| SharpCompress | 0.50.3 | 0.50.7 |
| System.CommandLine | 2.0.10 | 2.0.13 |
| xunit.v3 → xunit.v3.mtp-off | 3.2.2 | 4.0.2 |
| xunit.runner.visualstudio | 3.1.5 | 4.0.1 |
| YamlDotNet | absent | 18.1.0 |

The xUnit package variant preserves the repository's existing `dotnet test` runner workflow. Its upgrade removes a deprecated test-platform dependency. DirectML packages remain at their latest available versions, 1.24.4 and 0.14.1. CPU and CUDA runtime pairs follow the GenAI package's declared dependency versions. Managed/native pairs must not be independently advanced.

Variant checks found that the CUDA build incorrectly advertised DirectML. Its compiled capability and conditional tests now identify CUDA correctly. Exact Linux native-library imports in Runtime 1.31.0 and GenAI 0.18.0 require CUDA 13. The CUDA container therefore uses `nvidia/cuda:13.0.3-cudnn-runtime-ubuntu24.04` instead of the older CUDA 12.6 image. Deployment requires a host driver compatible with CUDA 13; container execution and real inference in this updated variant remain separate qualification work. See the [official supported NVIDIA image tags](https://gitlab.com/nvidia/container-images/cuda/-/blob/master/doc/supported-tags.md) and [ONNX Runtime CUDA requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html).

The installer helper's test-reference output now honors isolated build directories. This prevents processor-only and NVIDIA validation builds from overwriting the default test-reference output; normal build output locations are unchanged.

The default solution and tray package audits report no known vulnerabilities or deprecated packages after the update. Older transitive versions chosen by current parent packages remain, including DnsClient, Google.Protobuf, Castle.Core, localization components, logging components, and the intentionally paired DirectML managed assemblies. This is not a claim that every transitive package is the latest release.

## Verification and corpus status

The installed service used for ingestion identifies itself as `SaddleRAG 1.13.2+8ebcdf81bc3d07a3f2c62059de8809d9d0db4c3b`. The changes on this branch have not replaced it.

| Corpus | Verified content | Ingestion job |
| --- | --- | --- |
| `modelcontextprotocol-protocol / 2026-07-28` | 31 pages, 757 chunks, zero ingestion errors | `aa54b7b4-5bca-40d7-8445-dfed19c2b1d9` |
| `modelcontextprotocol-skills-extension / 2026-10-24` | 1 page, 25 chunks, zero ingestion errors | `2606ac7a-7d13-4593-8025-bf7f1546be70` |
| `modelcontextprotocol.aspnetcore / 2.3.0` | 334 SDK article pages / 668 chunks from the full crawl; one release-note page / three chunks added separately | `15de5137-48b2-43cc-a968-9dd5b6af9b96` |

The protocol inventory exactly matches all 31 specification source files under `docs/specification/2026-07-28/` at official repository commit `dae694f61a31f7a3ec2af3aff709e11f70f61e48`. The source list is preserved beside this report. Health checks detected no duplicate content in their samples and no chunk-boundary issues. Searches returned discovery, caching, transport-origin checks, and authorization-security text from their correct source documents. Metadata refresh `e6138e9c-4693-4505-9a4b-a0c588284780` processed all 757 chunks, rebuilt indexes, and completed without boundary issues.

The skills corpus contains the one official stable Skills page. Searches verified capability declarations, both methods, manifests, and digest security limits. Its retrieval-date version key does not claim an extension release number. Health found no duplicates or boundary issues; its one-page marker is expected. Metadata refresh `fc25b96d-7ac4-4c34-8f9a-836e314f77f7` processed all 25 chunks and rebuilt indexes.

The rejected HTML protocol snapshot was removed through the supported version-deletion operation and recreated from pinned official raw sources. The installed full-ingestion guard against overwriting Published versions was retained. No other library/version was deleted by this branch's ingestion work. The historical SDK 1.2.0 corpus remains.

The coordinated SDK ingestion covers all 330 official table-of-contents entries. Page inventory and exported data prove a final 335 pages / 671 chunks including the exact 2.3.0 release notes. Checks found no duplicate content or boundary issues. The release-note page became searchable after a supported profile reload, which confirmed the missing live-index refresh in the installed `add_page` path. Installed health/export summaries still show the earlier full-crawl totals of 334 / 668; those summary values are not the actual final inventory. SDK code examples are indexed as unfenced text, and some GitHub navigation remains on the release-note page. The SDK profile records those limitations without declaring incorrect language-mismatch evidence.

Raw-source limits: browser titles are empty, and Markdown frontmatter, components, and generated schema markup remain in the indexed text. Rendered diagrams, assets, and external schema artifacts were not ingested. The corpus is verified against the source manifest, not claimed to reproduce every rendered website element.

Completed checks include 215 focused default-build tests, 15 cancellation tests, four browser navigation regressions, and separate processor-only and NVIDIA builds with 65 and 54 focused tests respectively. The variant builds and restores produced zero warnings/errors and no known vulnerable or deprecated packages. These test counts describe separate runs and must not be added together as unique tests. The package assembly check rejected conflicting shared files, dependent runtimes, and incorrect runtime versions, then accepted the consistent fixture.

Initial completed verification (executable source at `8fe4111`):

| Check | Observed result |
| --- | --- |
| Clean Release solution rebuild | Passed; 0 warnings, 0 errors; 14.13 seconds |
| Clean Release tray rebuild, isolated outputs | Passed; 0 warnings, 0 errors; 2.70 seconds; retained because its source/configuration inputs remained unchanged |
| Complete normal `dotnet test` run | 2,631 passed, 24 skipped, 0 failed; 2,655 total; 1 minute 39 seconds |
| Real staged Phi-3 native test | Passed within the full suite; whole-test wall time including model setup and one generation: 8.102 seconds, 10:00:28.856–10:00:36.959 America/Denver on 2026-10-24 |
| Windows package assembly | Passed; 548 verified files, version `0.0.0-mcp-review`, bundled runtime `10.0.13` |
| Package assembly rejection fixtures | Passed: conflicting shared files, dependent runtime, and incorrect bundled-runtime version rejected; consistent files accepted |
| Dependency audits | Default solution, tray, processor-only and NVIDIA variants: no known vulnerable or deprecated packages at audit time |

The 24 full-suite skips were inspected individually: 17 require unstaged Nomic/mxbai model assets, three could not create directory symbolic links (`IOException`), two apply only to the processor-only build, and two are explicit opt-in document-conversion/classifier diagnostics. No models were downloaded or machine privileges changed to force these checks. The two processor-only contracts were covered by the earlier isolated variant run. Skips are not passes.

The processor-only and NVIDIA checks validate the updated package selections and conditional capability code. Their relevant package/capability inputs remained unchanged during later common ingestion repairs, so that evidence was reused. The complete latest source was rebuilt and tested in the default Windows configuration. No current Linux container or processor-only/NVIDIA native-inference qualification is claimed.

Exact final commands (all builds use two workers and tests four threads):

```powershell
dotnet build E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/SaddleRAG.slnx -c Release -t:Rebuild -m:2 -p:TreatWarningsAsErrors=true
dotnet build E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/SaddleRAG.Tray/SaddleRAG.Tray.csproj -c Release -t:Rebuild -m:2 -p:TreatWarningsAsErrors=true --artifacts-path E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/artifacts/validation-tray
dotnet test E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/SaddleRAG.slnx -c Release --no-build --no-restore -m:2 --logger "trx;LogFileName=full-tests.trx" --results-directory C:/Users/DougGerard/Documents/Codex/2026-10-24/referenced-chatgpt-conversation-this-is-an/work/test-results -- xUnit.MaxParallelThreads=4
pwsh -File E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/scripts/Test-PublishOutputs.ps1 -ScratchDirectory E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/artifacts/package-rules-check
pwsh -File E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/scripts/Publish-WindowsPackage.ps1 -OutputDirectory E:/GitHub/SaddleRAG.worktrees/mcp-spec-skills/artifacts/mcp-review-package -Version 0.0.0-mcp-review
```

The test command uses the official [xUnit test-run settings](https://xunit.net/docs/config-runsettings). Full results are preserved in `work/test-results/full-tests.trx` under the task directory. Focused results include `focused-tests-final.xml`, `crawl-publication-focused-results.xml`, `published-refresh-guidance-results.xml`, `published-refresh-no-history-results.xml`, `language-diagnostics-after.xml`, and `selector-lease-focused-results.xml`. The package manifest is `artifacts/mcp-review-package/publish/package-manifest.json` in the checkout. These files contain recorded results; overlapping focused-run totals are not added together.

## Ingestion failure disposition

All covering tests named below passed in the complete final run. This table distinguishes repaired source behavior from additional service-level qualification.

| Observed failure | Repair commit(s) and covering checks | Remaining limit |
| --- | --- | --- |
| Cancellation held classification work | `acb55c8`, `e526434`; active blocked-classifier cancellation, independent classifier cancellation, native-wrapper and Ollama cancellation cases | No composed queue test cancels an active HTTP generation then proves a same-library successor and unrelated library both progress. Source release paths were inspected. The passing real Phi-3 generation test does not prove active native cancellation. |
| Repeated links and extension aliases inflated the queue | `43bfac0`; ten real-browser queue cases plus canonical URL, retry, escalation, resume and depth checks | No replay of the live 130,000-entry frontier; no absolute frontier-size limit or shortest-route guarantee added. |
| Audit saturation was hidden as completion | `43bfac0`, `8fe4111`; real bounded-writer/crawler failure and typed audit exception through the publication fixture preserve the prior current version and mark the candidate failed | Persisted background-job status was not exercised through a complete live runner in this regression. Buffer capacity and stopping remain one admission diagnostic; repository exceptions remain distinct. |
| Published refresh preparation deleted data before refusal | `ee6e0f5`; forced/resumed/suspect refresh, correction preview/apply, publication race, ownership loss, no-history and profile/disposal checks | Preservation assertions verify no mutation calls against substitutes, not byte comparisons of a live database export. Preflight and queueing are not a single transaction. |
| Single-page search and counts stayed stale | `ee6e0f5`; whole-corpus vector inputs, text-index rebuild, exact profile, count-only metadata update, empty content and index-failure cases | Recording-vector tests prove refresh inputs; they do not execute an immediate real query. Repeated/forced replacement and concurrent additions retain their existing semantics and were not separately qualified. Installed summary counters remain stale until an authorized repair/refresh uses the updated code. |
| Navigation replaced or polluted article text | `e95c8b7`, `8fe4111`; navigation-preservation browser tests, six selection cases, caller-visible selection failure and classifier-input forwarding | Default extraction remains site-dependent. The installed release-note page retains navigation and its earlier category; no real-model categorization repair is claimed. |
| Untagged text was diagnosed as wrong language | `576af41`; unknown/empty/mixed language evidence, threshold, C# aliases and persisted suspect decisions | Source language-markup extraction and migration of saved suspect markers remain outside this correction. |
| Busy web ownership was described as directory ownership | `8fe4111`; five diagnostic cases and controlled-time active-owner/expiry tests for both source modes | No actual installed-service restart test; ownership duration and exclusion rules are unchanged. The separate reconnaissance-only first-ingestion defect is repaired in `acb55c8`. |

## Release review follow-up

Final review found that URL correction could acquire a new Web reservation without checking directory registration or protected partial data. Eight regression cases reproduced deletion being allowed before the ordinary ingestion path would refuse the target. Commit `76a322a` extracts the existing source-mode policy into `WebIngestionOwnership.CommitAsync` and applies it before URL cleanup while the lease is held. All 64 URL-correction and publication-lifecycle checks passed, including allowed reconnaissance-only and established-web states, lost ownership, named profiles, and refusal without destructive calls.

A fresh clean Release solution rebuild after that repair passed with zero warnings and errors in 18.04 seconds. The complete default test run passed 2,642 tests, skipped the same 24 environment-dependent cases described above, and failed none (2,666 total; 2 minutes 1 second). Evidence is `work/release-final-build.log` and `work/release-final-test-results/release-final-tests.trx` in the task directory. The commands are the same bounded build/test commands above with separate output filenames. Unchanged tray, dependency, variant, and packaging-rule evidence remains applicable; release packaging runs again in GitHub Actions.

Release-workflow review also found inconsistent manual-version handling in integration and coverage jobs, and an implicit default-branch target when creating a missing release tag. All version steps now consume the input through an environment variable and validate three numeric components before exposing outputs; draft creation explicitly targets the checked-out commit. This also prevents manual input from becoming executable shell text. Existing test selection, coverage collection, independent jobs, and tag-triggered release behavior are retained, with two build workers and four test threads.

The Linux delivery audit also found that the documented `install.sh` download was not uploaded as a release asset, the tar checksum named a build-directory path rather than the downloaded archive, and the advertised `uninstall.sh` was absent from the archive. The Linux packaging step now emits a basename-only checksum, includes the executable uninstaller, and uploads the install script. A harmless package fixture checks checksum verification from the download directory and archive membership; it does not execute either installer.

## Default-profile search index identity

Pre-install verification found a separate live defect: the three newly ingested MCP corpora were searchable with an omitted profile but missed their expected results with explicit `profile=local`. The same queries, library/version filters, and installed process were used; only the profile argument differed. All 43 older saved searches still passed with explicit `local`. Inventory checks confirmed the documents existed in the same configured database.

The database resolves an omitted profile to its configured active name, but the in-memory vector provider previously used the raw selector in its cache key. Ingestion, single-page updates, reloads, and deletion therefore changed only one spelling's cache. Startup populated both current-version caches, so restart could hide the symptom without preventing recurrence.

Commit `a02d62b` shares the default/empty selector's index with the valid configured active profile. All insert, exact/broad search, replacement, and removal paths use that identity. Genuinely different named profiles remain isolated; no equivalence is inferred from matching connection strings. Environment selection and invalid/empty-profile fallback reuse the database selection rules. Existing parameterless construction remains compatible; normal MCP and CLI dependency registration selects the configuration-aware constructor. Profile selection is captured at construction; unsupported in-process environment/settings mutation is not newly supported.

Sixteen alias regressions failed before repair while two isolation/compatibility checks passed. After repair, 88 focused checks passed, including 37 new provider/settings cases, with a zero-warning Release build. Independent source review found no blocker. The installed corpus was not reloaded, reindexed, or rewritten to obtain this evidence. Post-install checks will additionally require the three paired profile searches to agree.

The final clean solution rebuild after this change passed with zero warnings/errors in 15.95 seconds. The complete suite passed 2,679 tests, skipped the same 24 environment-dependent cases, and failed none (2,703 total; 1 minute 55 seconds). Evidence is `work/profile-alias-final-build.log` and `work/profile-alias-final-test-results/profile-alias-final-tests.trx`. The unchanged dependency/platform-package inputs retain their earlier audit evidence; GitHub and release packaging validate the final branch separately.

## Remaining deployment and research limits

Build and test success do not establish installation, remote deployment security, or native inference on every supported platform. Updated NVIDIA container execution and native inference remain deployment qualification work. The source validation above precedes release packaging and installation; their results must be recorded separately.

The separately authorized switch to the already installed Phi-3 classifier allowed documentation ingestion to finish. It does not explain the earlier Devstral inference delay. That latency investigation remains separate from the verified cancellation-propagation repair.
