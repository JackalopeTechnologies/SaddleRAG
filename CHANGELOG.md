# Changelog

All notable changes to SaddleRAG are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
