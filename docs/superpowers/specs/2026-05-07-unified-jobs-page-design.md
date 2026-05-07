# Unified Jobs Page — Design

**Date:** 2026-05-07
**Branch:** fix/jobs

## Problem

The web monitor's jobs page (`/monitor/jobs`) only shows full scrape jobs. Dry-run scrapes, rechunks, library renames, deletions, dependency indexing, URL corrections, and rescrubs are all invisible — they exist in the database (`BackgroundJobRecord` and `RescrubJobRecord` collections) but the page reads only from `IScrapeJobRepository`.

Live updates are similarly partial. `MonitorBroadcaster` is called only from `IngestionOrchestrator`, so SignalR `JobStarted`/`JobCompleted` events fire only for full scrapes. Dry-runs run silently from the UI's perspective even though the crawler underneath emits the same fetch/reject signals as a real scrape.

The user-facing symptom: a dry-run completes successfully, the result is queryable via `get_job_status`, but the monitor never lists the job at all. Restarting the browser doesn't help because nothing was ever sent to it.

## Goals

1. The jobs page lists every job, of every type, from every storage path.
2. Dry-runs have monitor parity with full scrapes — same SignalR events, same live row updates, same job-detail page (audit log feeds included).
3. Counted background jobs (rechunk, rescrub, index-deps) emit live progress; the row's count updates in place.
4. Binary background jobs (rename, delete-version, delete-library, url-correction) appear with start/complete/fail/cancel events and a `—` progress cell.
5. Existing `BackgroundJobRecord` rows in the DB show up retroactively — no migration.

## Non-Goals

- Unifying `ScrapeJobRecord`, `BackgroundJobRecord`, `RescrubJobRecord` into a single collection. Storage stays as it is.
- Introducing job retry, scheduling, or queueing semantics beyond what each runner already does.
- Changing any MCP tool's input or output shape.
- Cancellation for job types that don't already support it.

## Scope

| Job type | Storage | Has live events today | Has live events after | Progress unit |
|---|---|---|---|---|
| `scrape` | `ScrapeJobs` | yes | yes (unchanged) | pages indexed |
| `dryrun_scrape` | `BackgroundJobs` | no | yes | pages crawled |
| `rechunk` | `BackgroundJobs` | no | yes | chunks |
| `rescrub` | `RescrubJobs` | no | yes | chunks |
| `index_project_dependencies` | `BackgroundJobs` | no | yes | packages |
| `rename_library` | `BackgroundJobs` | no | yes (binary) | — |
| `delete_version` | `BackgroundJobs` | no | yes (binary) | — |
| `delete_library` | `BackgroundJobs` | no | yes (binary) | — |
| `submit_url_correction` | `BackgroundJobs` | no | yes (binary) | — |

---

## UX

### Column layout

| Created | Type | Target | Status | Progress | Duration | Job |
|---|---|---|---|---|---|---|

### Per-type rendering

| Type chip | Target column | Progress column |
|---|---|---|
| `Scrape` | `actipro-wpf @ 25.1` | `1,247 / ~1,500 pages indexed` |
| `Dry-run` | `actipro-wpf @ 25.1` | `50 / 50 pages crawled` |
| `Rechunk` | `actipro-wpf @ 25.1` | `8,140 / 9,200 chunks` |
| `Rescrub` | `actipro-wpf @ 25.1` | `8,140 / 9,200 chunks` |
| `Rename` | `aero-old → aero-new` | `—` |
| `Delete version` | `actipro-wpf @ 25.1` | `—` |
| `Delete library` | `actipro-wpf` (no version) | `—` |
| `Index deps` | `(scan: E:\proj\foo.sln)` | `5 / 12 packages` |
| `URL fix` | `actipro-wpf @ 25.1` | `—` |

### Type chip colors

- Read-only operations (Scrape, Dry-run, Index deps): `Color.Info`
- Mutate-only operations (Rechunk, Rescrub, URL fix): `Color.Primary`
- Destructive operations (Delete version, Delete library, Rename): `Color.Warning`

Status chip colors stay as today (`Running` info, `Completed` success, `Failed` error, `Cancelled` warning, `Queued` default).

### Filters

- **Status** (existing): `Queued`/`Running`/`Completed`/`Failed`/`Cancelled` or all.
- **Type** (new): single-select dropdown of job types or all.
- **Library contains** (existing): substring filter against the Target column's library id portion.
- **Limit** (existing): 50/100/500.

### Live behavior

- Running rows update in place via SignalR ticks. Progress cell, status chip, and duration tick continuously.
- New jobs prepend to the list when their `JobStarted` event arrives (subject to current filters).
- Terminal-state events (`JobCompleted`/`JobFailed`/`JobCancelled`) freeze the row at its final values.

### Job detail page

`/monitor/jobs/{id}` continues to work for every job type. For non-scrape jobs the page renders a stripped-down view: header (type, target, status, duration), progress section, result-JSON section (when present), and the audit-log feeds section ONLY when audit data exists for the job id (today: scrape and dry-run only).

---

## Architecture

### Read-side merge

New service `IUnifiedJobView` in `SaddleRAG.Monitor.Services`. Wraps the three existing repositories:

- `IScrapeJobRepository`
- `IBackgroundJobRepository`
- `IRescrubJobRepository`

Single method `ListAsync(ScrapeJobStatus? status, string? libraryFilter, JobType? typeFilter, int limit, CancellationToken ct)` that:

1. Issues all three repository calls in parallel via `Task.WhenAll`, each capped at `limit * 2` for headroom.
2. Projects each result type into a common `JobRow` record (defined below).
3. Applies in-memory filters (status / type / library substring).
4. Sorts by `CreatedAt` desc.
5. Truncates to `limit`.

Result-set sizes are small (hundreds, never millions), so in-memory merge/sort is fine and avoids the complexity of a unified DB view.

### `JobRow` record

```csharp
public sealed record JobRow
{
    public required string JobId { get; init; }
    public required JobType Type { get; init; }
    public required ScrapeJobStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    // Target — exactly one shape per type.
    public string? LibraryId { get; init; }
    public string? Version { get; init; }
    public string? RenameToId { get; init; }    // rename_library only
    public string? ScanPath { get; init; }      // index_project_dependencies only

    // Progress (zero/null for binary jobs).
    public int ItemsProcessed { get; init; }
    public int ItemsTotal { get; init; }
    public string? ItemsLabel { get; init; }    // "pages", "chunks", "packages"

    public int ErrorCount { get; init; }
    public string? ErrorMessage { get; init; }

    public TimeSpan? Duration =>
        StartedAt is null ? null : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
}

public enum JobType
{
    Scrape,
    DryRunScrape,
    Rechunk,
    Rescrub,
    RenameLibrary,
    DeleteVersion,
    DeleteLibrary,
    IndexProjectDependencies,
    SubmitUrlCorrection,
}
```

### `MonitorJobService` and `MonitorDataService` changes

`MonitorJobService.ListAsync` is rewritten to delegate to `IUnifiedJobView`. The existing `JobHistoryRow` projection becomes a thin adapter from `JobRow` to the page's display model — same outer signature, no breakage to the Razor page.

`MonitorDataService.GetJobInfoAsync` and `GetLatestJobIdAsync` are updated to call `IUnifiedJobView` so the per-job detail page resolves any job id.

### `BackgroundJobRecord` projection

Two background-job inputs need to be parsed from `InputJson` to populate the Target column:

- `rename_library`: extract `newId` → `JobRow.RenameToId`
- `index_project_dependencies`: extract `path` → `JobRow.ScanPath`

This is the only place we touch `InputJson`. Parse failures fall back to `null` and the column renders `(unknown)`.

---

## Live updates

### New broadcaster method and event

```csharp
// SaddleRAG.Core.Models.Monitor.JobProgressEvent
public sealed record JobProgressEvent
{
    public required string JobId { get; init; }
    public required int ItemsProcessed { get; init; }
    public required int ItemsTotal { get; init; }
    public required string ItemsLabel { get; init; }
}

// MonitorBroadcaster
public void RecordJobProgress(string jobId, int processed, int total, string label);
```

`MonitorLifecycleRelay` adds a `JobProgress` SignalR method alongside the existing four. The Blazor page subscribes and patches the matching row's progress cell.

### Per-runner plumbing

**Dry-run** (`IngestionTools.DryRunScrape`, around line 117):

```csharp
mBroadcaster.RecordJobStarted(record.Id, library, version, rootUrl);
try
{
    var report = await crawler.DryRunAsync(job, library, version, record.Id, ...);
    mBroadcaster.RecordJobCompleted(record.Id, indexedPageCount: 0);
    return report;
}
catch (OperationCanceledException) { mBroadcaster.RecordJobCancelled(record.Id); throw; }
catch (Exception ex)              { mBroadcaster.RecordJobFailed(record.Id, ex.Message); throw; }
```

The crawler already emits per-URL fetch/reject events through the broadcaster when given a job id, so no crawler change needed.

**Counted background jobs** (rechunk, index-deps): wrap `BackgroundJobRunner`'s existing `onProgress` callback to also call `RecordJobProgress`. Both the DB upsert and the SignalR fire happen in the same callback.

**Binary background jobs** (rename, delete-version, delete-library, url-correction): hook `BackgroundJobRunner.RunJobAsync` itself to call `RecordJobStarted` at the top of the try and `RecordJobCompleted/Failed/Cancelled` in the corresponding branches. One change covers all four. Library/version pass-through uses `jobRecord.LibraryId ?? string.Empty` and `jobRecord.Version ?? string.Empty`; `rootUrl` is empty string for non-crawl jobs.

**Rescrub** (`RescrubJobRunner`): same pattern as rechunk — wrap existing progress callback.

### Broadcaster guard relaxation

`MonitorBroadcaster.RecordJobStarted` today guards `libraryId`, `version`, and `rootUrl` with `ArgumentException.ThrowIfNullOrEmpty`. That assumes a scrape. For binary background jobs (rename, deps-index) any of those three can be legitimately empty. The guards become `ArgumentNullException.ThrowIfNull` — null still throws, but empty string is allowed. Tests assert both empty-string and populated cases.

The internal `JobState` queues (`pmRecentFetches`, `pmRecentRejects`) stay empty for non-crawl jobs and the broadcast tick emits zero counters — already correct.

---

## Edge cases

- **Job type discrimination**: `ScrapeJobRecord` rows project to `JobType.Scrape` directly. `RescrubJobRecord` rows project to `JobType.Rescrub` directly. `BackgroundJobRecord` rows parse the `JobType` string field with a static switch into one of the seven background variants. Unknown strings render `(unknown)` and don't crash the page.
- **Filter `libraryFilter` against jobs without a library** (rename, deps-index): row is excluded when the filter is non-empty.
- **Sort tiebreak**: equal `CreatedAt` falls back to `JobId` lexicographically. Deterministic for tests.
- **Concurrency in the merge**: each repository call uses its own cancellation-token-aware path; one slow repository doesn't block the others past the configured timeout.
- **Backfill**: pre-existing `BackgroundJobRecord` rows surface immediately on first page load — no migration, no batch job.
- **Live event for a job that doesn't match the current filter**: ignored client-side (no flicker).

---

## Testing

### `MonitorJobServiceTests` (extended)

- Rows from all three repositories merge into a single ordered list.
- Status filter applies post-merge across all sources.
- Type filter narrows to one source's contribution.
- Library substring filter excludes rows with no library.
- Limit caps the merged list, not each source.

### `UnifiedJobViewTests` (new)

- Unit-level tests against fakes for each repository.
- `JobRow` projection of every `BackgroundJobTypes` constant produces the expected shape.
- `InputJson` parse failures degrade gracefully.

### `BackgroundJobRunnerBroadcasterTests` (new)

- Binary jobs emit `JobStarted` once at start and exactly one terminal event.
- Counted jobs emit `JobProgress` for every `onProgress` callback invocation.
- Failed jobs emit `JobFailed` with the exception message.
- Cancelled jobs emit `JobCancelled`.

### `MonitorBroadcasterEventsTests` (extended)

- `RecordJobProgress` fires the `JobProgress` event with the right payload.

### `JobHistoryPageTests` (Bunit)

- Rendering of each `JobType` produces the expected Target and Progress cell text.
- Type chip color matches the spec table.
- Live `JobProgress` SignalR message updates the matching row's progress cell.

### Integration

- Run a dry-run end to end against an in-test Kestrel docs site. Assert: row appears in `/monitor/jobs`, progress updates, audit feeds populate on the detail page, terminal event freezes the row.

---

## Migration

None. This is a read-side and event-side change only. No collection schema changes, no data migration, no MCP tool surface change.

## Risk

- **Performance of three-way parallel reads**: bounded by the slowest of three small queries against indexed collections. Existing single-collection query is already non-blocking; adding two more in parallel is at worst 2× latency on a page that loads in tens of milliseconds.
- **`InputJson` shape drift**: parsing relies on field names that already exist for these job types. Any future change to those tools' input shape needs to update the parser; tests catch divergence.
- **SignalR event payload size**: `JobProgress` is four small fields. Negligible.
