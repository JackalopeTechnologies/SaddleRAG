# Scrape Diagnostics & Live Monitor

**Date:** 2026-05-02
**Branch:** feature/scrape-diagnostics-monitor

## Problem

A scrape that queued 52,891 URLs ultimately indexed 944. The other ~52,000 were filtered or pruned by the crawler before reaching the index. There is currently no visibility into:

- Which URLs were considered and why each was kept or dropped.
- What is happening inside a running job, in real time.
- Whether a finished scrape produced what the operator expected.

For a self-hosted user this means the surprise factor of "I expected page X but it isn't there" goes unanswered. For a future commercial user this means the system has no diagnostic surface at all — they cannot reason about what their filter patterns are doing.

This design adds a persistent audit log of every URL the crawler considered, a live browser-based monitor that watches a running job, and an MCP-surfaced inspection tool the LLM can use to answer "should I run this scrape?", "why did my scrape miss page X?", and "are my filter patterns too tight?".

## Scope

- New MongoDB collection `ScrapeAuditLog` capturing per-URL filter decisions and per-page outcomes.
- New ASP.NET Core endpoints inside the existing `SaddleRAG.Mcp` host (port 6100) serving a Blazor Server app at `/monitor`.
- New SignalR hub at `/monitor/hub` for live job updates pushed to the browser.
- New MCP tool `inspect_scrape` providing summary and drill-down access to the audit log.
- Behaviour change to `dryrun_scrape`: returns a `jobId` immediately instead of running synchronously; result obtained via `inspect_scrape`.
- Wyoming brown/gold MudBlazor theme (`#492F24` brown, `#FFC425` gold).
- Optional bearer-token auth on write endpoints (default open).

## Out of Scope

- Multi-ecosystem coverage tests (npm, pip, others) — separate spec, deferred to Project B.
- Charting (time-series throughput plots, per-stage flame graphs). Counters with deltas are enough for v1.
- User-management UI for tokens. A single shared token is enough for v1.
- Audit retention beyond auto-clear on rescrape. No TTL, no manual purge UI.
- Mutation of URL patterns or library profiles from the UI. Those remain MCP-only and are LLM-driven (the operator runs `recon_library` etc. via Claude Code).
- Live throughput charts beyond per-stage rate counters.

---

## Architecture

The MCP server is already an ASP.NET Core host (`SaddleRAG.Mcp`, port 6100, `ModelContextProtocol.AspNetCore`). The diagnostics surface lives in the same process so it shares the DI container, MongoDB connection, and crawl pipeline directly.

```
SaddleRAG.Mcp (ASP.NET Core, port 6100)
├─ /mcp                     existing MCP transport
├─ /health                  existing health endpoint
├─ /monitor                 NEW Blazor Server app
├─ /monitor/hub             NEW SignalR hub
└─ /api/monitor/...         NEW JSON endpoints (write actions)
```

A new project `SaddleRAG.Monitor` (Razor Class Library, Blazor components) is referenced by `SaddleRAG.Mcp`. MudBlazor is added as a NuGet dependency. The Wyoming theme is registered as a `MudTheme` instance.

A new singleton service `MonitorBroadcaster` is hosted alongside the ingestion pipeline. The pipeline writes events to it; the SignalR hub subscribes and forwards to connected clients. No DB read is required for live updates — the broadcaster keeps in-memory rolling buffers per active job.

A new singleton service `ScrapeAuditWriter` buffers audit-log entries from the pipeline and flushes batches to MongoDB.

---

## Data Model

### `ScrapeAuditLogEntry` (MongoDB collection `ScrapeAuditLog`)

```csharp
public class ScrapeAuditLogEntry
{
    public required string Id { get; init; }              // GUID string
    public required string JobId { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }

    public required string Url { get; init; }
    public string? ParentUrl { get; init; }
    public required string Host { get; init; }
    public int Depth { get; init; }

    public required DateTime DiscoveredAt { get; init; }

    public required AuditStatus Status { get; init; }
    public AuditSkipReason? SkipReason { get; init; }
    public string? SkipDetail { get; init; }              // e.g. matched pattern text

    public AuditPageOutcome? PageOutcome { get; init; }
}

public enum AuditStatus
{
    Considered,    // discovered, awaiting decision
    Skipped,       // dropped by a filter — see SkipReason
    Fetched,       // successfully fetched
    Failed,        // fetch error (timeout, 4xx, 5xx, navigation error)
    Indexed        // fully through pipeline, chunks committed
}

public enum AuditSkipReason
{
    PatternExclude,    // matched ExcludedUrlPatterns
    PatternMissAllowed,// did not match AllowedUrlPatterns
    BinaryExt,         // URL ended in non-doc extension (.pdf/.zip/etc.)
    OffSiteDepth,      // exceeded OffSiteDepth from rootUrl host
    SameHostDepth,     // exceeded SameHostDepth on root host
    HostGated,         // HostScopeFilter penalised this prefix
    AlreadyVisited,    // duplicate of an earlier discovery
    QueueLimit         // hit MaxPages cap
}

public class AuditPageOutcome
{
    public string? FetchStatus { get; init; }    // e.g. "200 OK"
    public string? Category { get; init; }        // DocCategory enum value
    public int? ChunkCount { get; init; }
    public string? Error { get; init; }
}
```

### Indexes

```
{ JobId: 1, Status: 1, SkipReason: 1 }    // bucketed queries
{ JobId: 1, Host: 1 }                      // by-host views
{ JobId: 1, Url: 1 }                       // single-URL forensics
```

### Retention

- Buffered batch inserts: flush every 500 entries or 1 second, whichever first.
- Auto-cleared on rescrape: when `scrape_docs` or `dryrun_scrape` begins for a `(libraryId, version)` that already has chunks, the existing cleanup path also runs `DeleteMany({ libraryId, version })` against `ScrapeAuditLog`.
- Failed and cancelled jobs keep their audit until the next rescrape, so the operator can still inspect the failure.
- Dryrun jobs share the cleanup path; a dryrun for `(library, version)` clears any prior audit for that pair before starting.

### Storage estimate

For a job similar to the Aerotech scrape (52K URLs considered, 944 indexed): ~50K filter rows + ~1K outcome rows ≈ 10–15 MB per scrape. Tolerable.

---

## Live Event Flow

### `MonitorBroadcaster` (singleton, in `SaddleRAG.Ingestion`)

A central event broker that holds in-memory state for every active job and pushes updates to subscribers (the SignalR hub). Per active job it tracks:

- Live counters (`PagesQueued, PagesFetched, PagesClassified, ChunksGenerated, ChunksEmbedded, PagesCompleted, ErrorCount`).
- Rolling buffer of last 50 fetched URLs (FIFO).
- Rolling buffer of last 50 rejected URLs with reason (FIFO).
- Current host being worked on.
- Errors accumulated since last flush.

### Tick events

Pushed every ~750 ms while a job is running. Payload:

```csharp
public class JobTickEvent
{
    public string JobId { get; init; }
    public DateTime At { get; init; }
    public PipelineCounters Counters { get; init; }
    public string? CurrentHost { get; init; }
    public IReadOnlyList<RecentFetch> RecentFetches { get; init; }     // last 50
    public IReadOnlyList<RecentReject> RecentRejects { get; init; }    // last 50
    public IReadOnlyList<RecentError> ErrorsThisTick { get; init; }
}
```

### Discrete events

Pushed immediately when they occur:

- `JobStartedEvent` — id, library, version, rootUrl.
- `JobCompletedEvent` — id, final counters, indexed page count.
- `JobFailedEvent` — id, error message.
- `JobCancelledEvent` — id, partial counters.
- `SuspectFlagEvent` — id, library, version, reasons.

The SignalR hub `MonitorHub` exposes `SubscribeJob(jobId)` and `SubscribeLanding()` (the landing page receives a coarser payload — just job lifecycle and an aggregate counter — to keep its render budget small).

The audit log is **not** read for live updates. It is forensic-only.

---

## UI Pages

The Blazor Server app lives in `SaddleRAG.Monitor`. Theme is registered globally with the Wyoming palette:

- Primary: `#492F24` (brown)
- Secondary: `#FFC425` (gold)
- Background: light cream tone derived from the brown
- Surface: standard MudBlazor surface

### `/monitor` — Landing (layout A)

Header: app title, navigation links (Libraries / Jobs / Settings).

**Active jobs strip** at top. For each running job:
- Library name and version, status (`fetching`, `paused`).
- Linear progress bar based on `PagesCompleted / max(PagesQueued, 1)`.
- Current counters in a compact row.
- `Cancel`, `Pause` buttons.

**Library card grid** below. Each card shows:
- Library id, current version.
- Chunk count, page count.
- Health indicator (`✓ healthy` / `⚠ suspect`).
- Hint text from `Library.Hint`.

Click an active job → `/monitor/jobs/{id}`.
Click a library → `/monitor/libraries/{id}`.

Empty state when no jobs running and no libraries: a single welcome card with a link to the docs and to `start_ingest` instructions.

### `/monitor/jobs/{id}` — Job detail (layout A)

Header: library/version, elapsed time, status, `Pause`/`Cancel` buttons.

**Pipeline strip**: five horizontal cells (Crawl, Classify, Chunk, Embed, Index), each showing absolute count and a per-second delta.

**Dual feeds** below in a 2-column grid:
- Recent fetches: scrolling list of the last 50 URLs, monospace.
- Recent rejects: same shape, with the skip reason labelled inline.

**Errors panel** at bottom. Hidden when zero. Shows last 20 errors with timestamp and message.

The page subscribes to `MonitorHub.SubscribeJob(jobId)` and re-renders on tick.

For a completed/failed/cancelled job, the page renders the final state from MongoDB and shows a banner indicating final status. The dual feeds are populated from the audit log instead of live buffers.

### `/monitor/libraries/{id}` — Library detail (layout B, tabbed)

Header (hero): library id, version, hint, last-scraped timestamp, health flag with reasons. Action buttons: `Rescrape`, `Rescrub`, `Delete version`. If a job is currently running on this library, a yellow banner links to `/monitor/jobs/{id}`.

Tabs:

- **Overview** — chunk/page counts, hostname distribution, language mix, boundary issue %, suspect reasons.
- **Profile** — languages, casing conventions, separators, callable shapes, likely symbols, confidence.
- **Audit** — summary view of the most recent audit (kept/dropped totals, top 5 reasons, top 5 hosts). Link to `/monitor/audits/{jobId}` for full inspection.
- **Versions** — list of all indexed versions with timestamps.

### `/monitor/audits/{jobId}` — Audit inspector

Filter controls at top: status, skip reason, host, free-text URL substring. Each filter is a chip that can be added/removed.

Below the filters:
- **Histogram strip** — counts per status and per skip reason for the active filter set.
- **URL list** — paged table of audit entries. Columns: URL, host, depth, status, skip reason, parent URL. Click a row to expand a detail panel showing parent URL, full skip detail, page outcome (if Indexed).

Reads directly from `ScrapeAuditLog` via aggregation pipelines. No SignalR.

### Empty / loading / error states

- Empty data → render a friendly "nothing here yet" card with the next-step suggestion.
- Hub disconnect → toast "live updates paused, reconnecting..." and switch to polling `/api/monitor/jobs/{id}/snapshot` every 5 s as a fallback. Resume push when the hub reconnects.
- Server error → MudBlazor snackbar with the error message.

---

## MCP Tool Surface

### New tool: `inspect_scrape`

```
inspect_scrape(
    jobId: string,
    status?: "Considered" | "Skipped" | "Fetched" | "Failed" | "Indexed",
    skipReason?: AuditSkipReason value,
    host?: string,
    url?: string,
    limit?: number = 50
) -> InspectScrapeResult
```

Behaviour:

- **No filters**: returns a top-level summary — kept/dropped totals, by-host breakdown, by-skip-reason histogram, sample URLs from each major bucket.
- **With filters**: returns matching audit entries (paged, capped by `limit`).
- **With `url`**: returns the single matching entry plus its lineage (parent → grandparent chain).

Returns null when `jobId` not found in either `ScrapeJobs` or `ScrapeAuditLog`.

### Behaviour change: `dryrun_scrape`

Currently runs synchronously and returns a `DryRunReport`. New behaviour: enqueues a background job and returns `{ JobId, Status: "Queued" }` immediately, matching `scrape_docs`. The caller polls `inspect_scrape(jobId)` (or `get_job_status` for lifecycle status). The dryrun pipeline writes audit entries the same way the real scrape does, but skips classify, chunk, embed, and index stages — so the audit log captures only the crawl-side decisions.

### No other tool changes

`scrape_docs`, `rescrub_library`, `get_class_reference`, `search_docs`, `list_libraries`, etc. are unchanged.

---

## Auth Strategy

Two endpoint groups:

- **Read endpoints** — `/monitor/*` Blazor pages, `/monitor/hub` SignalR connection, `GET /api/monitor/...`. Anonymous.
- **Write endpoints** — `POST /api/monitor/jobs/{id}/cancel`, `POST /api/monitor/jobs/{id}/pause`, `POST /api/monitor/jobs/{id}/resume`, `POST /api/monitor/libraries/{id}/rescrape`, `POST /api/monitor/libraries/{id}/rescrub`. Gated by an `AuthorizationPolicy` named `DiagnosticsWrite`.

The policy checks:
1. If `Diagnostics:WriteToken` is unset in configuration → policy succeeds (open). At application startup the host emits a single `Log.Warning` line: `"Diagnostics:WriteToken is unset — write endpoints are open. Set the token before exposing the monitor beyond localhost."`
2. If set → policy requires `Authorization: Bearer <token>` matching the configured value. Mismatch returns 401.

Blazor write actions read the configured token from server-side service state and include it implicitly on the JSON endpoint calls.

There is no UI for setting/changing the token in v1. It is a configuration concern.

---

## Pipeline Integration Points

The crawl pipeline currently filters at three points:

- `IsAllowed(url, job)` in `PageCrawler.cs` — pattern match check called from `EnqueueDiscoveredLinks`.
- `OffSiteDepth` / `SameHostDepth` check in `ProcessCrawlScopeAsync`.
- `HostScopeFilter.IsGated(url)` in `HandleCrawlEntryAsync`.

Each call site gains a single line that records an audit entry via `ScrapeAuditWriter.RecordSkipped(jobId, url, parentUrl, host, depth, reason, detail)`. Successful fetches call `RecordFetched(...)`; per-page pipeline completion calls `RecordIndexed(...)` with the page outcome; fetch errors call `RecordFailed(...)`.

The pipeline also notifies `MonitorBroadcaster` at the same call sites so live and audit stay in sync without a second event source.

`ScrapeJobRecord` gains a nullable `AuditEntryCount` field updated when the job completes, surfaced for sanity-checking in `get_scrape_status`.

---

## Testing Approach

Unit tests:

- `ScrapeAuditWriter` buffering, batching, flush-on-cancel.
- `MonitorBroadcaster` per-job state isolation, rolling buffer truncation, tick payload assembly.
- `inspect_scrape` MCP tool — summary, filtered, single-URL, missing-jobId paths.
- Auth policy — both unset (open) and set (token required) branches.

Integration tests:

- A small fake docs site is served from an in-test Kestrel host. A real scrape runs against it and the audit log is asserted to contain the expected mix of Considered/Skipped/Indexed rows for a known set of links.
- `dryrun_scrape` produces audit entries but no chunks/pages.
- Rescrape clears prior audit log for `(library, version)`.

UI smoke tests:

- Blazor component tests for landing, job detail, library detail, audit inspector — render with known data, assert key elements exist.
- One end-to-end Playwright test that opens `/monitor`, kicks off a small scrape via `inspect_scrape`'s sister setup, and verifies tick events render in the recent-fetches list.

This spec does not pre-decide whether multi-ecosystem coverage tests (npm, pip) ride along. They belong to Project B.

---

## Open Questions

None. All design decisions are settled.

## Future Work

- **Project B**: automated test coverage proving the symbol extractor, profile system, and chunker handle npm and pip docs as well as they handle NuGet/.NET. Separate spec.
- **Live throughput charts** — small sparkline per stage. Out of v1.
- **Multi-token auth** — replace single shared token with named tokens. Out of v1.
- **Audit retention policies** — configurable TTL or "keep last N jobs per library". Out of v1.
- **Monitor on a separate host** — currently `/monitor` lives in the same process as MCP. A future deployment may want a separate web host pointing at the same Mongo. Out of v1.
