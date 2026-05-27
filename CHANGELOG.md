# Changelog

All notable changes to SaddleRAG are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.2.0] - 2026-05-27

### License

- **Relicensed from dual AGPLv3 + commercial to MIT.** Single `LICENSE` file with
  the standard MIT text. The deprecated `COMMERCIAL-LICENSE.md` and `CLA.md` are
  preserved under `archive/` for historical reference but are no longer in effect
  for the current or any future version. Contributions no longer require a CLA;
  see `CONTRIBUTING.md`. Source-file license headers (510 files) updated to a
  two-line `SPDX-License-Identifier: MIT` form. The Windows installer's
  `License.rtf` regenerated in MIT form. (#99)

### Library packaging (import / export)

- **New MCP tools `export_library` and `import_library`** for sharing indexed
  libraries between SaddleRAG instances as a single `.srlib.zip` file. Closes
  the recurring "I scraped this for myself; can I hand it to a teammate without
  making them repeat the scrape?" gap. (#100)
- **Export** streams a Mongo-backed library into a temp-then-rename zip:
  `library.json`, per-version `version.json` / `profile.json` / `index.json` /
  `versionDiff.json` / `excludedSymbols.jsonl` / `pages.jsonl`, plus
  `chunks.jsonl` with embeddings stripped to a parallel `chunks.embeddings.f32`
  packed-float32 sidecar (row-aligned by construction). BM25 shards are bundled
  with their referenced GridFS spill blobs under `bm25/gridfs/{originalId}.bin`.
  `manifest.json` carries a sha256 + byte count for every blob plus per-version
  encoder metadata. Refuses to export when target versions span multiple
  embedding encoders.
- **Import** validates everything before any DB write: manifest schema version,
  library-id shape (no path traversal, no Mongo-illegal chars), per-blob sha256,
  per-blob byte count. Three pre-write gates: conflict (refuses on existing
  `(library, version)` unless `overwrite=true`), concurrent-job (refuses while
  any non-terminal job targets the same `(library, version)`), and encoder-match
  decision. On encoder match, chunks land with embeddings reconstructed from the
  f32 sidecar. On encoder mismatch, chunks land with `Embedding = null` and one
  `reembed_library` job is enqueued per imported version. BM25 GridFS blobs are
  re-uploaded under fresh ids on the receiver; shard `ShardGridFsRef` /
  `ExternalTerms` maps are rewritten in flight. Per-version rollback log
  unwinds every insert on mid-version failure; earlier successful versions in
  the same bundle stay imported. `overwrite=true` purges existing data before
  the write; `compact=true` runs `compact_collections` after a successful
  overwrite-import.
- **New `SaddleRAG.Packaging` project** sits between Core/Database and Mcp.
  Public surface: `LibraryExporter`, `LibraryImporter`, `BundleManifest`,
  `BundleVersionEntry`, `BlobInfo`, `BundleJsonOptions`, `VersionFilter`,
  request/result records. Internal: streaming `JsonlWriter<T>`/`JsonlReader<T>`,
  `EmbeddingBlobWriter`/`EmbeddingBlobReader`, `ManifestBuilder` with streaming
  sha256, `TeeStream`, zip bundle reader/writer.
- **`ICollectionCompactor` extracted from `compact_collections`** so the MCP
  tool and the import_library `compact=true` opt-in share one Mongo-side path.
  The MCP tool retains its JSON shaping; only the compact + stats logic moved.
- **`IBm25ShardRepository` gains four methods** for the export/import round-trip:
  `OpenGridFsBlobAsync`, `UploadGridFsBlobAsync`, `UpsertShardAsync`,
  `DeleteGridFsBlobAsync`. Implementations route through the existing
  `SaddleRagDbContext.Bm25Bucket`.
- **`LibraryIdValidator`** added to `SaddleRAG.Core` so the import path and any
  future writer share one regex (rejects empty, path traversal, Mongo-illegal
  characters, `system.` prefix).

## [1.1.0] - 2026-05-26

### SPA detector

- **Fixed false-positive SPA escalation on tutorial pages.**
  `EscalationController.DetectShellSignal` did naive substring matching against
  the full HTML response, so any documentation page that printed an HTML-escaped
  Blazor / React / Vue / Next / Nuxt / Svelte / Angular script tag inside
  `<pre>` / `<code>` / `<noscript>` blocks tripped the shell sniffer. Affected
  pages would swap to the SPA navigator on page 1, then stall (the SPA path
  could not extract more links from an already-server-rendered DOM). Hit while
  scraping the Ignite UI for Blazor docs, where the getting-started page prints
  `&lt;script src=&quot;_framework/blazor.server.js&quot;&gt;` inside a code
  block. Fix strips `<pre>`, `<code>`, and `<noscript>` content (case-insensitive,
  non-greedy, multiline) before substring matching. Real framework shells keep
  their bootstrap signals in `<script src=...>` attributes outside code blocks,
  so legitimate SPA detection is unaffected. (#95)

### MCP API consistency

- **Renamed `scrape_docs` parameter `libraryId` → `library`** to align with every
  other MCP tool (`dryrun_scrape`, `recon_library`, `get_library_health`,
  `submit_url_correction`, `rescrape_library`, `rechunk_library`,
  `reembed_library`, `reextract_library`, `list_libraries`, `list_pages`,
  `list_symbols`, `add_page`, `search_docs`, `get_class_reference`,
  `get_library_overview`, `get_version_changes`, `start_ingest`).
  The old `libraryId` keeps working as a deprecated alias for this release and
  emits a one-time warning per process. It will be removed in the next release.
- **Renamed `dryrun_scrape` parameter `rootUrl` → `url`** for the same reason.
  The old `rootUrl` keeps working as a deprecated alias with a one-time
  warning, and will be removed in the next release.
- Passing both names with **different values** now throws `ArgumentException`
  rather than silently picking one — fix the call site to pass `library` (or
  `url`) only.
- Skill resources (`saddlerag-scrape`, `saddlerag-scrape-strategy`,
  `saddlerag-recon`) updated to use the canonical names in every example.

### MCP description fixes

- `get_dashboard_index` `suggestedNextAction.tool` no longer returns the
  non-existent `cancel_scrape`. It now returns `cancel_job` (the actual tool
  name since the cancellation rework).
- `start_ingest`, `get_scrape_status`, `list_scrape_jobs` tool descriptions
  corrected to reference `cancel_job` rather than the retired `cancel_scrape`.
- README tool reference table updated for the same rename.

### Migration

For callers (LLMs, scripts, MCP clients):
```diff
- scrape_docs(url=..., libraryId="my-lib", version="current")
+ scrape_docs(url=..., library="my-lib", version="current")

- dryrun_scrape(rootUrl=..., library="my-lib", version="current")
+ dryrun_scrape(url=..., library="my-lib", version="current")
```

Both forms work in this release. Update at your own pace; the alias warning
in the server log shows which calls still use the deprecated names.
