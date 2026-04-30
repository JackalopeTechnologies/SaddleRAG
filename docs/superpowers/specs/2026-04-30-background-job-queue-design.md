# Background Job Queue — Shared Infrastructure for Long-Running MCP Tools

**Date:** 2026-04-30
**Branch:** feature/claude-plugin-installer

## Problem

Six MCP tools block the transport connection for minutes at a time. The MCP transport drops connections after 60–300 seconds, killing long operations mid-stream. The existing stopgap (standalone `CancellationTokenSource` with a 30-minute timeout) keeps the operation alive server-side but the client loses the result on disconnect.

The right fix is the same pattern already used by `scrape_docs` and `rescrub_library`: queue the operation, return a job ID immediately, let the caller poll for completion.

## Scope

This design covers six operations:

| Tool | Operation | Duration |
|------|-----------|----------|
| `rechunk_library` | Re-embeds every page through the chunker | 10 min – 1 hr |
| `rename_library` (apply=true) | Cascade updates across 8 MongoDB collections | 5 – 60 s |
| `delete_version` (apply=true) | Cascade deletes across all collections | 10 s – 2 min |
| `delete_library` (apply=true) | Cascade deletes all versions | 10 s – 2 min |
| `index_project_dependencies` | NuGet/npm/pip registry scan + resolution | 1 – 5 min |
| `dryrun_scrape` | Playwright crawl of the full docs site | 30 s – 5 min |
| `submit_url_correction` (apply=true) | Cascade delete + queues new scrape job | 10 – 30 s |

`scrape_docs` and `rescrub_library` are already async and are not in scope.

## Out of Scope

- Migrating `ScrapeJobRecord` or `RescrubJobRecord` to the new infrastructure
- Job cancellation for the new operations (none of these have natural mid-stream cancel points)
- Job retry on failure
- Persistence of `InputJson` beyond display purposes

---

## Data Model

### `BackgroundJobRecord` (SaddleRAG.Core/Models)

```csharp
public class BackgroundJobRecord
{
    // Identity
    public required string Id { get; init; }        // GUID string
    public required string JobType { get; init; }   // see JobTypes constants
    public string? Profile { get; init; }
    public string? LibraryId { get; init; }         // null for dryrun_scrape, index_project_dependencies
    public string? Version { get; init; }           // null for rename_library, delete_library,
                                                    //   dryrun_scrape, index_project_dependencies
    public required string InputJson { get; init; } // serialized input params

    // Status
    public ScrapeJobStatus Status { get; set; } = ScrapeJobStatus.Queued;
    public string PipelineState { get; set; } = nameof(ScrapeJobStatus.Queued);

    // Generic progress (rechunk → "chunks", dep-indexer → "packages", dryrun → "pages")
    // Binary-status operations leave these at zero and ItemsLabel null
    public int ItemsProcessed { get; set; }
    public int ItemsTotal { get; set; }
    public string? ItemsLabel { get; set; }

    // Outcome
    public string? ErrorMessage { get; set; }
    public string? ResultJson { get; set; }         // JSON-serialized result; set on Completed

    // Timestamps
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}
```

**`JobType` constants** (defined as string constants in `BackgroundJobRecord` or a companion static class):
```
"rechunk"
"rename_library"
"delete_version"
"delete_library"
"dryrun_scrape"
"index_project_dependencies"
"submit_url_correction"
```

Reuses the existing `ScrapeJobStatus` enum (Queued/Running/Completed/Failed/Cancelled) — the values are generic enough to apply.

**MongoDB collection:** `backgroundJobs` — separate from `scrapeJobs` and `rescrubJobs`.

---

## Infrastructure

### `IBackgroundJobRepository` (SaddleRAG.Core/Interfaces)

```csharp
Task UpsertAsync(BackgroundJobRecord job, CancellationToken ct = default);
Task<BackgroundJobRecord?> GetAsync(string id, CancellationToken ct = default);
Task<IReadOnlyList<BackgroundJobRecord>> ListRecentAsync(
    string? jobType = null, int limit = 20, CancellationToken ct = default);
```

### `BackgroundJobRepository` (SaddleRAG.Database/Repositories)

MongoDB implementation using `ReplaceOneAsync(IsUpsert: true)` — identical pattern to `ScrapeJobRepository` and `RescrubJobRepository`.

`ListRecentAsync` filters by `JobType` when provided, sorts by `CreatedAt` descending, limits to `limit`.

### `RepositoryFactory`

Add `GetBackgroundJobRepository(string? profile = null)`.

### `SaddleRagDbContext`

Add `IMongoCollection<BackgroundJobRecord> BackgroundJobs` property, collection name `"backgroundJobs"`.

### `BackgroundJobRunner` (SaddleRAG.Ingestion)

The runner accepts a **delegate** rather than a separate executor interface per job type. This avoids creating six executor classes while keeping domain logic in the calling tool.

```csharp
public class BackgroundJobRunner
{
    // Queue a job and immediately return its Id.
    // execute receives: (record, onProgress(processed, total), cancellationToken)
    // The runner manages the full Queued → Running → Completed/Failed/Cancelled lifecycle.
    public async Task<string> QueueAsync(
        BackgroundJobRecord jobRecord,
        Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute,
        CancellationToken ct = default);
}
```

**Lifecycle inside `RunJobAsync`:**
1. Set `Status = Running`, `StartedAt`, `PipelineState = "Running"` → upsert
2. Build `onProgress` callback: updates `ItemsProcessed`, `ItemsTotal`, `LastProgressAt` → upserts record synchronously (same pattern as `RescrubJobRunner`)
3. Call `execute(record, onProgress, mAppStoppingToken)`
4. On success: set `Status = Completed`, `PipelineState = "Completed"`, `CompletedAt` → upsert
5. On `OperationCanceledException`: set `Status = Cancelled`, `CancelledAt`, `CompletedAt` → upsert
6. On `Exception`: set `Status = Failed`, `ErrorMessage`, `PipelineState = "Failed"`, `CompletedAt` → upsert

No per-library semaphore needed (unlike `ScrapeJobRunner`) — these operations are one-shot and the caller controls sequencing through the tool's own `dryRun` check.

---

## Service Changes

### `RechunkService.RechunkAsync`

Add `Action<int, int>? onProgress = null` parameter after `RechunkOptions options`, before `CancellationToken ct`. Thread it through to the inner chunk-processing loop (same pattern applied to `RescrubService.ProcessChunksAsync`). Invoke `onProgress?.Invoke(i + 1, chunks.Count)` after each chunk.

Update `RechunkService` test call sites with named `ct:` parameter.

### `PageCrawler.DryRunAsync`

Add `Action<int, int>? onProgress = null` parameter before `CancellationToken ct`. The dry-run crawler already iterates pages; invoke `onProgress?.Invoke(pagesVisited, maxPages)` after each page fetch so the `BackgroundJobRecord` shows live progress.

### `DependencyIndexer.IndexProjectAsync`

Add `Action<int, int>? onProgress = null` parameter before `CancellationToken ct`. Invoke `onProgress?.Invoke(packagesProcessed, packagesTotal)` after each package is resolved. `packagesTotal` may not be known upfront; pass the running count as both arguments until the total is determined, then switch to `(processed, known_total)`.

The mutation logic (delete/rename) runs as database batch operations with no meaningful mid-stream progress — the executor lambda calls them directly with no progress callback.

---

## MCP Surface

### New tools

#### `get_job_status(jobId, profile?)`

Returns the full `BackgroundJobRecord` fields. When `Status == Completed` and `ResultJson` is set, `ResultJson` is parsed and inlined as `Result` in the response. When `JobType == "rechunk"` and the result is present, adds a `BoundaryHint` object (same `pct` + `hint` logic as `get_rescrub_status`).

Does **not** cover scrape jobs or rescrub jobs — those have their own status tools.

#### `list_jobs(jobType?, limit, profile?)`

Lists recent background jobs. Returns: `Id`, `Status`, `JobType`, `LibraryId`, `Version`, `ItemsProcessed`, `ItemsTotal`, `ItemsLabel`, `CreatedAt`, `CompletedAt`. Optionally filtered by `jobType`.

Both new tools live in a new `BackgroundJobTools.cs` file.

### Modified tools

#### `rechunk_library`

Remove synchronous `service.RechunkAsync(...)` call. Inject `BackgroundJobRunner`. Build a `BackgroundJobRecord` with `JobType = "rechunk"`, `LibraryId`, `Version`, `ItemsLabel = "chunks"`, `InputJson`. Call `runner.QueueAsync(record, execute, ct)` where the execute lambda calls `rechunkService.RechunkAsync(...)` with the progress callback and serializes the `RechunkResult` into `record.ResultJson`. Return `{ JobId, Status: "Queued" }`.

#### `rename_library`

`dryRun = true` path is unchanged (sync, returns preview JSON immediately).

`dryRun = false` path: inject `BackgroundJobRunner`. Build record with `JobType = "rename_library"`, `LibraryId`. Execute lambda runs the existing rename logic. Return `{ JobId, Status: "Queued" }`.

#### `delete_version`

Same pattern as `rename_library`. `dryRun = true` stays sync. `dryRun = false` queues with `JobType = "delete_version"`, `LibraryId`, `Version`.

#### `delete_library`

Same. `dryRun = false` queues with `JobType = "delete_library"`, `LibraryId`.

#### `submit_url_correction`

`dryRun = true` stays sync. `dryRun = false` queues with `JobType = "submit_url_correction"`, `LibraryId`, `Version`. Execute lambda runs the cascade delete then calls `scrapeJobRunner.QueueAsync(...)` and stores the resulting scrape `JobId` in `record.ResultJson` so the caller can chain to `get_scrape_status`.

#### `dryrun_scrape`

Always queues (Playwright crawl is never fast). Inject `BackgroundJobRunner`. Build record with `JobType = "dryrun_scrape"`, `ItemsLabel = "pages"`. Execute lambda calls `crawler.DryRunAsync(...)` passing the progress callback and serializes the `DryRunReport` into `record.ResultJson`. Return `{ JobId, Status: "Queued" }`.

#### `index_project_dependencies`

Always queues. `JobType = "index_project_dependencies"`, `ItemsLabel = "packages"`. Execute lambda calls `indexer.IndexProjectAsync(...)` passing the progress callback. Serialize `DependencyIndexReport` into `record.ResultJson`.

---

## DI Registration (Program.cs)

```csharp
builder.Services.AddSingleton<BackgroundJobRunner>();
```

`BackgroundJobRunner` depends on `RepositoryFactory` and `IHostApplicationLifetime` (already registered). No other new singletons needed — each tool's executor lambda closes over its already-registered services.

---

## File Inventory

**New files (5):**
- `SaddleRAG.Core/Models/BackgroundJobRecord.cs`
- `SaddleRAG.Core/Interfaces/IBackgroundJobRepository.cs`
- `SaddleRAG.Database/Repositories/BackgroundJobRepository.cs`
- `SaddleRAG.Ingestion/BackgroundJobRunner.cs`
- `SaddleRAG.Mcp/Tools/BackgroundJobTools.cs`

**Modified files (12):**
- `SaddleRAG.Database/SaddleRagDbContext.cs` — add `BackgroundJobs` collection
- `SaddleRAG.Database/Repositories/RepositoryFactory.cs` — add `GetBackgroundJobRepository()`
- `SaddleRAG.Ingestion/Recon/RechunkService.cs` — add `onProgress` callback
- `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` — add `onProgress` callback to `DryRunAsync`
- `SaddleRAG.Ingestion/Scanning/DependencyIndexer.cs` — add `onProgress` callback to `IndexProjectAsync`
- `SaddleRAG.Mcp/Tools/RechunkTools.cs` — convert to async job
- `SaddleRAG.Mcp/Tools/MutationTools.cs` — convert apply-paths to async job
- `SaddleRAG.Mcp/Tools/UrlCorrectionTools.cs` — convert apply-path to async job
- `SaddleRAG.Mcp/Tools/IngestionTools.cs` — convert `dryrun_scrape` to async job
- `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` — convert `index_project_dependencies` to async job
- `SaddleRAG.Mcp/Program.cs` — register `BackgroundJobRunner`
- `SaddleRAG.Tests/...` — update `RechunkService` test call sites

---

## Testing

Existing `RescrubServiceTests` test the `onProgress` callback pattern — same approach applies to verifying the `RechunkService` callback. No new integration tests are required; the runner lifecycle (Queued → Running → Completed/Failed) follows the same pattern already validated by `RescrubJobRunner` usage.
