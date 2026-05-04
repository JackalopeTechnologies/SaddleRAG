# Scrape Diagnostics Wave 2 — Live Monitor + Deferred Fixes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Blazor Server live-monitor UI at `/monitor`, a SignalR hub for real-time job updates, and a `MonitorBroadcaster` singleton that the pipeline writes to — plus five prioritized deferred fixes from Wave 1.

**Architecture:** `SaddleRAG.Monitor` (new Razor Class Library) is referenced by `SaddleRAG.Mcp`, which gains Blazor Server + SignalR endpoints alongside the existing MCP endpoint. A `MonitorBroadcaster` singleton in `SaddleRAG.Ingestion` keeps per-job in-memory state and triggers 750 ms tick events that the `MonitorHub` forwards to connected browsers. Deferred fixes ship first because the histogram page and lineage view depend on them.

**Tech Stack:** C# / .NET 10, MongoDB.Driver, MudBlazor 8.x, Microsoft.AspNetCore.SignalR, Blazor Server, xUnit, bUnit, Microsoft.Playwright.

---

## Spec Reference

`docs/superpowers/specs/2026-05-02-scrape-diagnostics-monitor-design.md` — sections "Architecture" through "Testing Approach".

## Conventions (apply to every new file)

1. **File header**:
   ```csharp
   // FileName.cs
   // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
   // SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
   // Available under AGPLv3 (see LICENSE) or a commercial license
   // (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.
   ```
2. `#region Usings` block immediately under the file header.
3. Single namespace per file, matching folder structure.
4. Allman braces; 4-space indent; max 120 char lines.
5. Field prefixes: `m` (instance), `ps` (private static), `sm` (static readonly).
6. No early returns — use the variable pattern. No `continue` — use `Where` or if-block. No if/else chains — use switch expressions.
7. Tests: xUnit `[Fact]`, `Assert.*`, namespace mirrors source folder under `SaddleRAG.Tests`.
8. All `dotnet build` commands use `.slnx`: `dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true`

---

## Phase A — Deferred Fixes (priority: ship before any Phase B work)

---

## Task 1: Fix NormalizeUrl port-stripping bug

**Files:**
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` (line ~2026)
- Modify: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs` (add port test)

**The bug:** `NormalizeUrl` at line 2026 builds `$"{uri.Scheme}://{uri.Host}{path}"` — `uri.Port` is omitted, so any doc site served on a non-default port (e.g. an in-test Kestrel host on a random port) has its port silently stripped, making every URL unresolvable.

- [ ] **Step 1: Write the failing test**

Add to `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs` (or a new `NormalizeUrlTests.cs` unit test class if you prefer — a public wrapper is easiest):

```csharp
// SaddleRAG.Tests/Crawling/NormalizeUrlTests.cs
// (standard file header)

using System.Reflection;
using SaddleRAG.Ingestion.Crawling;

namespace SaddleRAG.Tests.Crawling;

public sealed class NormalizeUrlTests
{
    // NormalizeUrl is private static — expose via reflection for testing.
    private static string? Invoke(string url, bool keepExtension = false)
    {
        var method = typeof(PageCrawler).GetMethod("NormalizeUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?) method.Invoke(null, [url, keepExtension]);
    }

    [Fact]
    public void NonDefaultPortIsPreservedAfterNormalization()
    {
        string? result = Invoke("http://localhost:8080/docs/page.html");
        Assert.Equal("http://localhost:8080/docs/page", result);
    }

    [Fact]
    public void DefaultHttpPortIsNotAppended()
    {
        string? result = Invoke("http://example.com:80/docs/page.html");
        Assert.Equal("http://example.com/docs/page", result);
    }

    [Fact]
    public void DefaultHttpsPortIsNotAppended()
    {
        string? result = Invoke("https://example.com:443/docs/page");
        Assert.Equal("https://example.com/docs/page", result);
    }
}
```

- [ ] **Step 2: Run the test to verify failure**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~NormalizeUrlTests"
```
Expected: FAIL — `NonDefaultPortIsPreservedAfterNormalization` fails; port is stripped.

- [ ] **Step 3: Apply the fix**

In `PageCrawler.cs`, change line 2026 from:
```csharp
var normalized = $"{uri.Scheme}://{uri.Host}{path}";
```
to:
```csharp
string portSuffix = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
var normalized = $"{uri.Scheme}://{uri.Host}{portSuffix}{path}";
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~NormalizeUrlTests"
```
Expected: all three PASS.

- [ ] **Step 5: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

Write `msg.txt`:
```
fix(crawl): preserve non-default port in NormalizeUrl
```
```
git add SaddleRAG.Ingestion/Crawling/PageCrawler.cs SaddleRAG.Tests/Crawling/NormalizeUrlTests.cs
git commit -F msg.txt
```

---

## Task 2: Fix ScrapeAuditWriter channel reader race (timing flakiness)

**Files:**
- Modify: `SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs`
- Modify: `SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs`

**The bug:** `ScrapeAuditWriter` creates its channel with `SingleReader = true`, but both `RunFlushLoopAsync` (background) and `FlushAsync` (called from tests and DisposeAsync) read from the same channel concurrently. With `SingleReader = true` the channel reader is not thread-safe for concurrent reads, so items can be silently lost or observations can race, causing occasional count mismatches in `FlushesWhenBufferReachesSizeThreshold`.

- [ ] **Step 1: Write a deterministic replacement for the flaky test**

Replace the existing `FlushesWhenBufferReachesSizeThreshold` test body in `ScrapeAuditWriterTests.cs` so the test doesn't rely on timing or concurrent readers:

```csharp
[Fact]
public async Task FlushesWhenBufferReachesSizeThreshold()
{
    var spy = new SpyRepository();
    // Use batchSize=5. The background loop will fire when 5 items land in the channel.
    // We await DisposeAsync which drains everything, giving a deterministic count.
    var writer = new ScrapeAuditWriter(spy, batchSize: 5,
                                       flushInterval: TimeSpan.FromMinutes(10));

    for (var i = 0; i < 5; i++)
        writer.RecordSkipped(NewCtx("job-1"), $"https://a.com/{i}",
                             parentUrl: null, host: "a.com", depth: 1,
                             AuditSkipReason.PatternExclude, detail: null);

    await writer.DisposeAsync();   // flush-on-dispose drains the channel

    Assert.Equal(5, spy.Inserted.Count);
    Assert.All(spy.Inserted, e => Assert.Equal(AuditStatus.Skipped, e.Status));
}
```

- [ ] **Step 2: Run tests to confirm they currently pass or identify exact failure**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditWriterTests"
```
Note which tests fail (expected: intermittent, may pass or fail).

- [ ] **Step 3: Fix the channel options**

In `SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs`, change the channel creation from:
```csharp
mChannel = Channel.CreateUnbounded<ScrapeAuditLogEntry>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = false
});
```
to:
```csharp
mChannel = Channel.CreateUnbounded<ScrapeAuditLogEntry>(new UnboundedChannelOptions
{
    SingleReader = false,
    SingleWriter = false
});
```

- [ ] **Step 4: Run the full test suite for the writer**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditWriterTests"
```
Expected: all tests PASS, repeatedly (run twice to confirm no flakiness).

- [ ] **Step 5: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
fix(audit): change audit channel to SingleReader=false to fix concurrent-read race
```
```
git add SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs
git commit -F msg.txt
```

---

## Task 3: Replace SummarizeAsync with Mongo $group pipeline

**Files:**
- Modify: `SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs`
- Run: `SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs` (existing tests must still pass)

**The problem:** `SummarizeAsync` currently calls `QueryAsync(jobId, ..., int.MaxValue)` which loads every audit row into memory. For a 52K-row job that's ~15 MB of BSON over the wire. Replace with a `$group` aggregation so only counts travel across the wire.

- [ ] **Step 1: Run existing tests to confirm green baseline**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditRepositoryTests"
```
Expected: PASS.

- [ ] **Step 2: Replace SummarizeAsync with aggregation pipeline**

In `SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs`, replace the entire `SummarizeAsync` method with:

```csharp
/// <inheritdoc />
public async Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);

    // ── status bucket pipeline ───────────────────────────────────────────────
    var statusPipeline = new[]
    {
        new BsonDocument("$match", new BsonDocument("JobId", jobId)),
        new BsonDocument("$group", new BsonDocument
        {
            { "_id",        new BsonDocument("Status",     "$Status") },
            { "count",      new BsonDocument("$sum", 1) },
            { "skipReason", new BsonDocument("$first", "$SkipReason") }
        })
    };

    // ── host bucket pipeline ─────────────────────────────────────────────────
    var hostPipeline = new[]
    {
        new BsonDocument("$match", new BsonDocument("JobId", jobId)),
        new BsonDocument("$group", new BsonDocument
        {
            { "_id",   "$Host" },
            { "count", new BsonDocument("$sum", 1) }
        })
    };

    // ── skip-reason bucket pipeline ──────────────────────────────────────────
    var reasonPipeline = new[]
    {
        new BsonDocument("$match", new BsonDocument
        {
            { "JobId",      jobId },
            { "SkipReason", new BsonDocument("$exists", true) }
        }),
        new BsonDocument("$group", new BsonDocument
        {
            { "_id",   "$SkipReason" },
            { "count", new BsonDocument("$sum", 1) }
        })
    };

    var col = mContext.ScrapeAuditLog;
    var statusTask = col.Aggregate<BsonDocument>(statusPipeline, cancellationToken: ct).ToListAsync(ct);
    var hostTask   = col.Aggregate<BsonDocument>(hostPipeline,   cancellationToken: ct).ToListAsync(ct);
    var reasonTask = col.Aggregate<BsonDocument>(reasonPipeline, cancellationToken: ct).ToListAsync(ct);

    await Task.WhenAll(statusTask, hostTask, reasonTask);

    var statusBuckets = statusTask.Result;
    var hostBuckets   = hostTask.Result;
    var reasonBuckets = reasonTask.Result;

    int total    = 0;
    int indexed  = 0;
    int fetched  = 0;
    int failed   = 0;
    int skipped  = 0;

    foreach (var doc in statusBuckets)
    {
        int cnt    = doc["count"].AsInt32;
        total     += cnt;
        var status = (AuditStatus) doc["_id"]["Status"].AsInt32;
        switch (status)
        {
            case AuditStatus.Indexed:  indexed = cnt; break;
            case AuditStatus.Fetched:  fetched = cnt; break;
            case AuditStatus.Failed:   failed  = cnt; break;
            case AuditStatus.Skipped:  skipped = cnt; break;
        }
    }

    var skipReasonCounts = reasonBuckets.ToDictionary(
        d => (AuditSkipReason) d["_id"].AsInt32,
        d => d["count"].AsInt32);

    var hostCounts = hostBuckets.ToDictionary(
        d => d["_id"].IsBsonNull ? string.Empty : d["_id"].AsString,
        d => d["count"].AsInt32);

    return new AuditSummary
    {
        JobId           = jobId,
        TotalConsidered = total,
        IndexedCount    = indexed,
        FetchedCount    = fetched,
        FailedCount     = failed,
        SkippedCount    = skipped,
        SkipReasonCounts = skipReasonCounts,
        HostCounts       = hostCounts
    };
}
```

Add `using MongoDB.Bson;` to the usings region if not already present.

- [ ] **Step 3: Run existing repository tests**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditRepositoryTests"
```
Expected: PASS — `SummarizeReturnsBucketedCounts` still green with real Mongo aggregation.

- [ ] **Step 4: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
perf(audit): replace SummarizeAsync in-memory scan with Mongo $group pipeline
```
```
git add SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs
git commit -F msg.txt
```

---

## Task 4: Add ParentUrl to CrawlEntry

**Files:**
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` (private CrawlEntry record + all construction sites + EnqueueDiscoveredLinks)
- Modify: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs` (assert ParentUrl populated)

`CrawlEntry` is a private record inside `PageCrawler` at line 34:
```csharp
private record CrawlEntry(string Url, int InScopeDepth, int SameHostDepth, int OffSiteDepth, int RetryAttemptIndex = 0);
```
It has no `ParentUrl`. Audit entries produced for skipped URLs do carry `parentEntry.Url` (that works via `EnqueueDiscoveredLinks`), but fetch/indexed records that go through the channel see `null` parentUrl because `CrawlEntry` never carries it.

- [ ] **Step 1: Add a failing assertion to an existing integration test**

In `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`, find the integration test that verifies `PatternExclude` rows. Add an assertion:

```csharp
// After asserting the skipped rows exist, also assert parentUrl is non-null:
Assert.All(skippedRows, row => Assert.NotNull(row.ParentUrl));
```

Run:
```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"
```
Expected: FAIL — ParentUrl is null on audit rows that should have a parent.

- [ ] **Step 2: Extend CrawlEntry with ParentUrl**

Change the private record declaration (line 34) from:
```csharp
private record CrawlEntry(string Url,
                          int InScopeDepth,
                          int SameHostDepth,
                          int OffSiteDepth,
                          int RetryAttemptIndex = 0);
```
to:
```csharp
private record CrawlEntry(string Url,
                          int InScopeDepth,
                          int SameHostDepth,
                          int OffSiteDepth,
                          int RetryAttemptIndex = 0,
                          string? ParentUrl = null);
```

- [ ] **Step 3: Update CrawlEntry construction sites in EnqueueDiscoveredLinks**

In `EnqueueDiscoveredLinks` (around line 1557), each `new CrawlEntry(...)` creates a child entry without a parent URL. Update all three construction sites to pass the parent URL:

```csharp
var child = linkInScope switch
{
    true => new CrawlEntry(normalized,
                           parentEntry.InScopeDepth + 1,
                           SameHostDepth: 0,
                           OffSiteDepth: 0,
                           ParentUrl: parentEntry.Url),
    false => linkSameHost
                 ? new CrawlEntry(normalized,
                                  parentEntry.InScopeDepth,
                                  parentEntry.SameHostDepth + 1,
                                  parentEntry.OffSiteDepth,
                                  ParentUrl: parentEntry.Url)
                 : new CrawlEntry(normalized,
                                  parentEntry.InScopeDepth,
                                  parentEntry.SameHostDepth,
                                  parentEntry.OffSiteDepth + 1,
                                  ParentUrl: parentEntry.Url)
};
```

- [ ] **Step 4: Update audit call sites that now have access to entry.ParentUrl**

Grep for `RecordFetched` and `RecordFailed` calls in `PageCrawler.cs`:
```
grep -n "RecordFetched\|RecordFailed" SaddleRAG.Ingestion/Crawling/PageCrawler.cs
```
For each call that currently passes `parentUrl: null` or `entry.ParentUrl` (whichever it is), confirm it now passes `entry.ParentUrl` (since `CrawlEntry` now carries it). It should already be using `entry.ParentUrl` from the audit wiring in Wave 1 — if it wasn't, update those call sites now.

Similarly for `RecordSkipped` in `EnqueueDiscoveredLinks` — it already passes `parentEntry.Url` directly, which is correct. No change needed there.

- [ ] **Step 5: Run the integration tests**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"
```
Expected: PASS.

- [ ] **Step 6: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
fix(audit): add ParentUrl to CrawlEntry so all audit records carry lineage
```
```
git add SaddleRAG.Ingestion/Crawling/PageCrawler.cs SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
git commit -F msg.txt
```

---

## Task 5: Plumb Depth + ParentUrl through PageRecord → DocChunk → RecordIndexed

**Files:**
- Modify: `SaddleRAG.Core/Models/PageRecord.cs`
- Modify: `SaddleRAG.Core/Models/DocChunk.cs`
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` (where PageRecord is constructed from CrawlEntry)
- Modify: `SaddleRAG.Ingestion/Chunking/CategoryAwareChunker.cs` (copy PageRecord fields to DocChunk)
- Modify: `SaddleRAG.Ingestion/IngestionOrchestrator.cs` (use chunk fields in RecordIndexed)
- Modify: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs` (assert depth/parent on indexed rows)

**Goal:** `RecordIndexed` currently hard-codes `parentUrl: null, depth: 0`. After this task it uses real values from the chunk.

- [ ] **Step 1: Add Depth and ParentUrl to PageRecord**

In `SaddleRAG.Core/Models/PageRecord.cs`, add after the `ContentHash` property:

```csharp
    /// <summary>
    ///     Crawl depth at which this page was discovered (0 = root).
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    ///     URL of the page that linked to this one, if known.
    /// </summary>
    public string? ParentUrl { get; init; }
```

- [ ] **Step 2: Add Depth and ParentUrl to DocChunk**

In `SaddleRAG.Core/Models/DocChunk.cs`, add after the `ParserVersion` property:

```csharp
    /// <summary>
    ///     Crawl depth of the source page (0 = root). Carried from PageRecord.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    ///     URL of the page that linked to the source page, if known.
    /// </summary>
    public string? ParentUrl { get; init; }
```

- [ ] **Step 3: Populate Depth and ParentUrl when PageCrawler creates PageRecord**

Grep for `new PageRecord` in `PageCrawler.cs`:
```
grep -n "new PageRecord" SaddleRAG.Ingestion/Crawling/PageCrawler.cs
```
At each construction site, add:
```csharp
Depth     = entry.InScopeDepth,   // use the appropriate depth counter
ParentUrl = entry.ParentUrl
```

`entry` is the `CrawlEntry` for the current crawl step. Use `entry.InScopeDepth` as the depth value (it represents how deep within the root scope this page is — 0 for the root entry).

- [ ] **Step 4: Copy Depth and ParentUrl when CategoryAwareChunker creates DocChunk**

Grep for `new DocChunk` in `SaddleRAG.Ingestion/Chunking/CategoryAwareChunker.cs`:
```
grep -n "new DocChunk" SaddleRAG.Ingestion/Chunking/CategoryAwareChunker.cs
```
For each construction site that builds from a `PageRecord`, add:
```csharp
Depth     = page.Depth,
ParentUrl = page.ParentUrl
```

- [ ] **Step 5: Use chunk fields in IngestionOrchestrator.RecordIndexed**

In `SaddleRAG.Ingestion/IngestionOrchestrator.cs`, `RunIndexStageAsync`, change the `AccumulatePageChunkCounts` helper to also track depth and parentUrl per page. Add a parallel dictionary alongside `pageChunkCounts`:

```csharp
var pageMetadata = new Dictionary<string, (int Depth, string? ParentUrl)>(StringComparer.OrdinalIgnoreCase);
```

In `AccumulatePageChunkCounts`, add population (or inline it — read the existing method at line 832 and extend it):
```csharp
private static void AccumulatePageChunkCounts(DocChunk[] chunks,
                                               HashSet<string> indexedPageUrls,
                                               Dictionary<string, (int ChunkCount, DocCategory Category)> pageChunkCounts,
                                               Dictionary<string, (int Depth, string? ParentUrl)> pageMetadata)
{
    foreach (var chunk in chunks)
    {
        indexedPageUrls.Add(chunk.PageUrl);
        if (pageChunkCounts.TryGetValue(chunk.PageUrl, out var existing))
            pageChunkCounts[chunk.PageUrl] = (existing.ChunkCount + 1, chunk.Category);
        else
            pageChunkCounts[chunk.PageUrl] = (1, chunk.Category);

        pageMetadata.TryAdd(chunk.PageUrl, (chunk.Depth, chunk.ParentUrl));
    }
}
```

Update the `RecordIndexed` loop to use the metadata:

```csharp
foreach (var (pageUrl, (chunkCount, category)) in pageChunkCounts)
{
    var host = new Uri(pageUrl).Host;
    pageMetadata.TryGetValue(pageUrl, out var meta);
    mAuditWriter.RecordIndexed(auditCtx,
                               pageUrl,
                               parentUrl: meta.ParentUrl,
                               host,
                               depth: meta.Depth,
                               new AuditPageOutcome
                               {
                                   FetchStatus = null,
                                   Category    = category.ToString(),
                                   ChunkCount  = chunkCount
                               });
}
```

Update all call sites of `AccumulatePageChunkCounts` to pass the new `pageMetadata` dict.

- [ ] **Step 6: Add assertion to integration test for indexed row depth**

In `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`, in the test that asserts indexed rows, add:

```csharp
var indexedRows = auditRows.Where(r => r.Status == AuditStatus.Indexed).ToList();
Assert.All(indexedRows, row => Assert.True(row.Depth >= 0));
// Root page has depth 0; linked pages have depth >= 1
var linkedIndexedRow = indexedRows.FirstOrDefault(r => r.ParentUrl != null);
Assert.NotNull(linkedIndexedRow);
```

- [ ] **Step 7: Run all audit integration tests**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"
```
Expected: PASS.

- [ ] **Step 8: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
fix(audit): plumb Depth and ParentUrl through PageRecord and DocChunk to RecordIndexed
```
```
git add SaddleRAG.Core/Models/PageRecord.cs SaddleRAG.Core/Models/DocChunk.cs SaddleRAG.Ingestion/Chunking/CategoryAwareChunker.cs SaddleRAG.Ingestion/Crawling/PageCrawler.cs SaddleRAG.Ingestion/IngestionOrchestrator.cs SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
git commit -F msg.txt
```

---

## Phase B — MonitorBroadcaster

---

## Task 6: MonitorBroadcaster core types

**Files:**
- Create: `SaddleRAG.Core/Models/Monitor/PipelineCounters.cs`
- Create: `SaddleRAG.Core/Models/Monitor/JobTickEvent.cs`
- Create: `SaddleRAG.Core/Models/Monitor/JobLifecycleEvents.cs`
- Create: `SaddleRAG.Core/Models/Monitor/RecentFetch.cs`
- Create: `SaddleRAG.Core/Models/Monitor/RecentReject.cs`
- Create: `SaddleRAG.Core/Models/Monitor/RecentError.cs`
- Create: `SaddleRAG.Tests/Monitor/MonitorEventTypeTests.cs`

- [ ] **Step 1: Write the type-stability test**

```csharp
// SaddleRAG.Tests/Monitor/MonitorEventTypeTests.cs
// (standard file header)

using System.Text.Json;
using SaddleRAG.Core.Models.Monitor;

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorEventTypeTests
{
    [Fact]
    public void JobTickEventRoundTripsThroughJson()
    {
        var tick = new JobTickEvent
        {
            JobId         = "job-1",
            At            = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            Counters      = new PipelineCounters { PagesQueued = 10, PagesFetched = 5 },
            CurrentHost   = "example.com",
            RecentFetches = [new RecentFetch { Url = "https://example.com/a" }],
            RecentRejects = [new RecentReject { Url = "https://example.com/b",
                                                Reason = "PatternExclude" }],
            ErrorsThisTick = []
        };

        string json  = JsonSerializer.Serialize(tick);
        var roundTrip = JsonSerializer.Deserialize<JobTickEvent>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("job-1",        roundTrip!.JobId);
        Assert.Equal(10,             roundTrip.Counters.PagesQueued);
        Assert.Single(roundTrip.RecentFetches);
        Assert.Single(roundTrip.RecentRejects);
    }

    [Fact]
    public void JobStartedEventRoundTripsThroughJson()
    {
        var evt = new JobStartedEvent
        {
            JobId     = "job-2",
            LibraryId = "lib",
            Version   = "1.0",
            RootUrl   = "https://docs.example.com/"
        };

        string json      = JsonSerializer.Serialize(evt);
        var roundTrip    = JsonSerializer.Deserialize<JobStartedEvent>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("lib", roundTrip!.LibraryId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorEventTypeTests"
```
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the types**

```csharp
// SaddleRAG.Core/Models/Monitor/PipelineCounters.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record PipelineCounters
{
    public int PagesQueued       { get; init; }
    public int PagesFetched      { get; init; }
    public int PagesClassified   { get; init; }
    public int ChunksGenerated   { get; init; }
    public int ChunksEmbedded    { get; init; }
    public int PagesCompleted    { get; init; }
    public int ErrorCount        { get; init; }
}
```

```csharp
// SaddleRAG.Core/Models/Monitor/RecentFetch.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentFetch
{
    public required string Url { get; init; }
    public DateTime At { get; init; }
}
```

```csharp
// SaddleRAG.Core/Models/Monitor/RecentReject.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentReject
{
    public required string Url    { get; init; }
    public required string Reason { get; init; }
    public DateTime At { get; init; }
}
```

```csharp
// SaddleRAG.Core/Models/Monitor/RecentError.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentError
{
    public required string Message { get; init; }
    public DateTime At { get; init; }
}
```

```csharp
// SaddleRAG.Core/Models/Monitor/JobTickEvent.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobTickEvent
{
    public required string             JobId          { get; init; }
    public required DateTime           At             { get; init; }
    public required PipelineCounters   Counters       { get; init; }
    public string?                     CurrentHost    { get; init; }
    public IReadOnlyList<RecentFetch>  RecentFetches  { get; init; } = [];
    public IReadOnlyList<RecentReject> RecentRejects  { get; init; } = [];
    public IReadOnlyList<RecentError>  ErrorsThisTick { get; init; } = [];
}
```

```csharp
// SaddleRAG.Core/Models/Monitor/JobLifecycleEvents.cs
// (standard file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobStartedEvent
{
    public required string JobId     { get; init; }
    public required string LibraryId { get; init; }
    public required string Version   { get; init; }
    public required string RootUrl   { get; init; }
}

public sealed record JobCompletedEvent
{
    public required string           JobId          { get; init; }
    public required PipelineCounters FinalCounters  { get; init; }
    public required int              IndexedPageCount { get; init; }
}

public sealed record JobFailedEvent
{
    public required string JobId        { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record JobCancelledEvent
{
    public required string           JobId          { get; init; }
    public required PipelineCounters PartialCounters { get; init; }
}

public sealed record SuspectFlagEvent
{
    public required string              JobId     { get; init; }
    public required string              LibraryId { get; init; }
    public required string              Version   { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}
```

- [ ] **Step 4: Run tests to verify pass**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorEventTypeTests"
```
Expected: PASS.

- [ ] **Step 5: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
feat(monitor): add MonitorBroadcaster event types and PipelineCounters
```
```
git add SaddleRAG.Core/Models/Monitor SaddleRAG.Tests/Monitor/MonitorEventTypeTests.cs
git commit -F msg.txt
```

---

## Task 7: IMonitorBroadcaster interface + MonitorBroadcaster service

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs`
- Create: `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`
- Create: `SaddleRAG.Tests/Monitor/MonitorBroadcasterTests.cs`

- [ ] **Step 1: Write the failing unit tests**

```csharp
// SaddleRAG.Tests/Monitor/MonitorBroadcasterTests.cs
// (standard file header)

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Diagnostics;

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorBroadcasterTests
{
    [Fact]
    public void RecordFetchAddsToRecentFeedAndIncrementsCounter()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-1", "lib", "1.0", "https://x.com/");
        broadcaster.RecordFetch("job-1", "https://x.com/page");

        var snapshot = broadcaster.GetJobSnapshot("job-1");
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Counters.PagesFetched);
        Assert.Single(snapshot.RecentFetches);
        Assert.Equal("https://x.com/page", snapshot.RecentFetches[0].Url);
    }

    [Fact]
    public void RecentFeedCapAt50Entries()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-2", "lib", "1.0", "https://y.com/");

        for (var i = 0; i < 60; i++)
            broadcaster.RecordFetch("job-2", $"https://y.com/{i}");

        var snapshot = broadcaster.GetJobSnapshot("job-2");
        Assert.NotNull(snapshot);
        Assert.Equal(50, snapshot!.RecentFetches.Count);
    }

    [Fact]
    public void GetJobSnapshotReturnsNullForUnknownJob()
    {
        var broadcaster = new MonitorBroadcaster();
        Assert.Null(broadcaster.GetJobSnapshot("no-such-job"));
    }

    [Fact]
    public void JobCompletedClearsActiveJobFromBroadcaster()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-3", "lib", "1.0", "https://z.com/");
        broadcaster.RecordJobCompleted("job-3", indexedPageCount: 5);

        Assert.Null(broadcaster.GetJobSnapshot("job-3"));
    }

    [Fact]
    public void SubscriberReceivesTickEvent()
    {
        var broadcaster   = new MonitorBroadcaster();
        JobTickEvent? got = null;
        broadcaster.Subscribe("job-4", tick => { got = tick; return Task.CompletedTask; });

        broadcaster.RecordJobStarted("job-4", "lib", "1.0", "https://w.com/");
        broadcaster.RecordFetch("job-4", "https://w.com/page");
        broadcaster.BroadcastTick("job-4");

        Assert.NotNull(got);
        Assert.Equal("job-4", got!.JobId);
        Assert.Equal(1, got.Counters.PagesFetched);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorBroadcasterTests"
```
Expected: FAIL.

- [ ] **Step 3: Create IMonitorBroadcaster interface**

```csharp
// SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs
// (standard file header)

#region Usings
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Records live pipeline events and pushes them to SignalR subscribers.
/// </summary>
public interface IMonitorBroadcaster
{
    void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl);
    void RecordFetch(string jobId, string url);
    void RecordReject(string jobId, string url, string reason);
    void RecordError(string jobId, string message);
    void RecordPageClassified(string jobId);
    void RecordChunkGenerated(string jobId);
    void RecordChunkEmbedded(string jobId);
    void RecordPageCompleted(string jobId);
    void RecordJobCompleted(string jobId, int indexedPageCount);
    void RecordJobFailed(string jobId, string errorMessage);
    void RecordJobCancelled(string jobId);
    void RecordSuspectFlag(string jobId, string libraryId, string version, IReadOnlyList<string> reasons);

    JobTickSnapshot? GetJobSnapshot(string jobId);
    IReadOnlyList<string> GetActiveJobIds();

    void Subscribe(string jobId, Func<JobTickEvent, Task> handler);
    void Unsubscribe(string jobId, Func<JobTickEvent, Task> handler);

    void BroadcastTick(string jobId);
}
```

Add `JobTickSnapshot` to `SaddleRAG.Core/Models/Monitor/JobTickEvent.cs` (append at end of file, same namespace):
```csharp
public sealed record JobTickSnapshot
{
    public required string             JobId         { get; init; }
    public required PipelineCounters   Counters      { get; init; }
    public required IReadOnlyList<RecentFetch>  RecentFetches  { get; init; }
    public required IReadOnlyList<RecentReject> RecentRejects  { get; init; }
    public required IReadOnlyList<RecentError>  RecentErrors   { get; init; }
    public string? CurrentHost { get; init; }
}
```

- [ ] **Step 4: Implement MonitorBroadcaster**

```csharp
// SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs
// (standard file header)

#region Usings
using System.Collections.Concurrent;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

public sealed class MonitorBroadcaster : IMonitorBroadcaster
{
    private sealed class JobState
    {
        public required string JobId     { get; init; }
        public required string LibraryId { get; init; }
        public required string Version   { get; init; }
        public required string RootUrl   { get; init; }

        public int PagesQueued     { get; set; }
        public int PagesFetched    { get; set; }
        public int PagesClassified { get; set; }
        public int ChunksGenerated { get; set; }
        public int ChunksEmbedded  { get; set; }
        public int PagesCompleted  { get; set; }
        public int ErrorCount      { get; set; }
        public string? CurrentHost { get; set; }

        public readonly Queue<RecentFetch>  RecentFetches = new();
        public readonly Queue<RecentReject> RecentRejects = new();
        public readonly Queue<RecentError>  RecentErrors  = new();
        public readonly object Lock = new();
    }

    private const int RecentFeedCapacity = 50;
    private const int ErrorFeedCapacity  = 20;

    private readonly ConcurrentDictionary<string, JobState> mJobs = new();
    private readonly ConcurrentDictionary<string, List<Func<JobTickEvent, Task>>> mSubscribers = new();

    public void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl)
    {
        var state = new JobState
        {
            JobId     = jobId,
            LibraryId = libraryId,
            Version   = version,
            RootUrl   = rootUrl
        };
        mJobs[jobId] = state;
    }

    public void RecordFetch(string jobId, string url)
    {
        if (!mJobs.TryGetValue(jobId, out var state)) return;
        lock (state.Lock)
        {
            state.PagesFetched++;
            state.PagesQueued++;
            state.CurrentHost = SafeGetHost(url);
            EnqueueCapped(state.RecentFetches, new RecentFetch { Url = url, At = DateTime.UtcNow },
                          RecentFeedCapacity);
        }
    }

    public void RecordReject(string jobId, string url, string reason)
    {
        if (!mJobs.TryGetValue(jobId, out var state)) return;
        lock (state.Lock)
            EnqueueCapped(state.RecentRejects,
                          new RecentReject { Url = url, Reason = reason, At = DateTime.UtcNow },
                          RecentFeedCapacity);
    }

    public void RecordError(string jobId, string message)
    {
        if (!mJobs.TryGetValue(jobId, out var state)) return;
        lock (state.Lock)
        {
            state.ErrorCount++;
            EnqueueCapped(state.RecentErrors,
                          new RecentError { Message = message, At = DateTime.UtcNow },
                          ErrorFeedCapacity);
        }
    }

    public void RecordPageClassified(string jobId)  => Increment(jobId, s => s.PagesClassified++);
    public void RecordChunkGenerated(string jobId)  => Increment(jobId, s => s.ChunksGenerated++);
    public void RecordChunkEmbedded(string jobId)   => Increment(jobId, s => s.ChunksEmbedded++);
    public void RecordPageCompleted(string jobId)   => Increment(jobId, s => s.PagesCompleted++);

    public void RecordJobCompleted(string jobId, int indexedPageCount) => mJobs.TryRemove(jobId, out _);
    public void RecordJobFailed(string jobId, string errorMessage)    => mJobs.TryRemove(jobId, out _);
    public void RecordJobCancelled(string jobId)                      => mJobs.TryRemove(jobId, out _);

    public void RecordSuspectFlag(string jobId, string libraryId, string version,
                                  IReadOnlyList<string> reasons) { }  // placeholder — hub publishes directly

    public JobTickSnapshot? GetJobSnapshot(string jobId)
    {
        if (!mJobs.TryGetValue(jobId, out var state)) return null;
        lock (state.Lock)
        {
            return new JobTickSnapshot
            {
                JobId        = jobId,
                CurrentHost  = state.CurrentHost,
                Counters     = BuildCounters(state),
                RecentFetches = state.RecentFetches.ToList(),
                RecentRejects = state.RecentRejects.ToList(),
                RecentErrors  = state.RecentErrors.ToList()
            };
        }
    }

    public IReadOnlyList<string> GetActiveJobIds() =>
        mJobs.Keys.ToList();

    public void Subscribe(string jobId, Func<JobTickEvent, Task> handler)
    {
        var list = mSubscribers.GetOrAdd(jobId, _ => []);
        lock (list)
            list.Add(handler);
    }

    public void Unsubscribe(string jobId, Func<JobTickEvent, Task> handler)
    {
        if (!mSubscribers.TryGetValue(jobId, out var list)) return;
        lock (list)
            list.Remove(handler);
    }

    public void BroadcastTick(string jobId)
    {
        var snapshot = GetJobSnapshot(jobId);
        if (snapshot is null) return;

        var tick = new JobTickEvent
        {
            JobId          = jobId,
            At             = DateTime.UtcNow,
            Counters       = snapshot.Counters,
            CurrentHost    = snapshot.CurrentHost,
            RecentFetches  = snapshot.RecentFetches,
            RecentRejects  = snapshot.RecentRejects,
            ErrorsThisTick = snapshot.RecentErrors
        };

        if (!mSubscribers.TryGetValue(jobId, out var handlers)) return;
        List<Func<JobTickEvent, Task>> snapshot2;
        lock (handlers)
            snapshot2 = [..handlers];

        foreach (var handler in snapshot2)
            _ = handler(tick);   // fire-and-forget per subscriber
    }

    private void Increment(string jobId, Action<JobState> mutate)
    {
        if (!mJobs.TryGetValue(jobId, out var state)) return;
        lock (state.Lock)
            mutate(state);
    }

    private static void EnqueueCapped<T>(Queue<T> queue, T item, int cap)
    {
        queue.Enqueue(item);
        while (queue.Count > cap)
            queue.Dequeue();
    }

    private static PipelineCounters BuildCounters(JobState s) => new()
    {
        PagesQueued     = s.PagesQueued,
        PagesFetched    = s.PagesFetched,
        PagesClassified = s.PagesClassified,
        ChunksGenerated = s.ChunksGenerated,
        ChunksEmbedded  = s.ChunksEmbedded,
        PagesCompleted  = s.PagesCompleted,
        ErrorCount      = s.ErrorCount
    };

    private static string SafeGetHost(string url)
    {
        string result = string.Empty;
        try { result = new Uri(url).Host; }
        catch (UriFormatException) { }
        return result;
    }
}
```

- [ ] **Step 5: Run tests to verify pass**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorBroadcasterTests"
```
Expected: PASS.

- [ ] **Step 6: Build and commit**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Write `msg.txt`:
```
feat(monitor): add IMonitorBroadcaster interface and MonitorBroadcaster implementation
```
```
git add SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs SaddleRAG.Core/Models/Monitor SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs SaddleRAG.Tests/Monitor/MonitorBroadcasterTests.cs
git commit -F msg.txt
```

---

## Task 8: Wire MonitorBroadcaster into the pipeline + DI

**Files:**
- Modify: `SaddleRAG.Ingestion/IngestionOrchestrator.cs`
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs`
- Modify: `SaddleRAG.Mcp/Program.cs`

MonitorBroadcaster hooks into the same call sites as the audit writer. The pipeline already calls `mAuditWriter.RecordFetched` etc. — add `mBroadcaster.RecordFetch` alongside each one.

- [ ] **Step 1: Inject IMonitorBroadcaster into IngestionOrchestrator**

In `IngestionOrchestrator`, add the dependency alongside `IScrapeAuditWriter`. Grep for the constructor:
```
grep -n "IScrapeAuditWriter\|ScrapeAuditWriter" SaddleRAG.Ingestion/IngestionOrchestrator.cs
```
Add `IMonitorBroadcaster broadcaster` to the constructor, store as `mBroadcaster`.

- [ ] **Step 2: Add broadcaster calls in IngestionOrchestrator**

In `RunIndexStageAsync`, after each `mAuditWriter.RecordIndexed(...)`, add:
```csharp
mBroadcaster.RecordPageCompleted(auditCtx.JobId);
```

In `RunClassifyStageAsync`, after each page is sent to the classify→chunk channel, add:
```csharp
mBroadcaster.RecordPageClassified(auditCtx.JobId);
```

In `RunChunkStageAsync`, after each `DocChunk[]` batch is produced, add one call per page:
```csharp
mBroadcaster.RecordChunkGenerated(auditCtx.JobId);
```

In `RunEmbedStageAsync`, after each batch is embedded, add:
```csharp
mBroadcaster.RecordChunkEmbedded(auditCtx.JobId);
```

At the top of the orchestrator's main pipeline entry (the method that calls all five stages), emit the lifecycle events:
```csharp
mBroadcaster.RecordJobStarted(progress.Id, job.LibraryId, job.Version, job.RootUrl);
```
In the `finally` / completion block, emit:
```csharp
// on success:
mBroadcaster.RecordJobCompleted(progress.Id, indexedPageCount: progress.PagesCompleted);
// on failure (catch Exception):
mBroadcaster.RecordJobFailed(progress.Id, ex.Message);
// on cancellation (catch OperationCanceledException):
mBroadcaster.RecordJobCancelled(progress.Id);
```

- [ ] **Step 3: Inject IMonitorBroadcaster into PageCrawler**

Add the dependency to `PageCrawler`'s constructor. Grep:
```
grep -n "IScrapeAuditWriter" SaddleRAG.Ingestion/Crawling/PageCrawler.cs
```
Add `IMonitorBroadcaster broadcaster` alongside; store as `mBroadcaster`.

After each `mAuditWriter.RecordFetched(...)` call in `HandleCrawlEntryAsync`, add:
```csharp
mBroadcaster.RecordFetch(auditCtx.JobId, entry.Url);
```
After each `mAuditWriter.RecordSkipped(...)` call, add:
```csharp
mBroadcaster.RecordReject(auditCtx.JobId, entry.Url, skipReason.Value.Reason.ToString());
```
After each `mAuditWriter.RecordFailed(...)` call, add:
```csharp
mBroadcaster.RecordError(auditCtx.JobId, error);
```

- [ ] **Step 4: Register in DI**

In `SaddleRAG.Mcp/Program.cs`, after the `IScrapeAuditWriter` registration:
```csharp
builder.Services.AddSingleton<MonitorBroadcaster>();
builder.Services.AddSingleton<IMonitorBroadcaster>(sp =>
    sp.GetRequiredService<MonitorBroadcaster>());
```
Add `using SaddleRAG.Ingestion.Diagnostics;` to the usings region if needed.

- [ ] **Step 5: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors. Run the full test suite:
```
dotnet test SaddleRAG.slnx
```
Expected: PASS.

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(monitor): wire MonitorBroadcaster into ingestion pipeline and PageCrawler
```
```
git add SaddleRAG.Ingestion SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Phase C — Blazor Foundation

---

## Task 9: Create SaddleRAG.Monitor Razor Class Library

**Files:**
- Create: `SaddleRAG.Monitor/SaddleRAG.Monitor.csproj`
- Create: `SaddleRAG.Monitor/_Imports.razor`
- Create: `SaddleRAG.Monitor/Theme/WyomingTheme.cs`
- Modify: `SaddleRAG.slnx` (add project)
- Modify: `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj` (add ProjectReference)

- [ ] **Step 1: Create the project file**

```xml
<!-- SaddleRAG.Monitor/SaddleRAG.Monitor.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AddRazorSupportForMvc>false</AddRazorSupportForMvc>
    <RazorLangVersion>latest</RazorLangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="8.*" />
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.*" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SaddleRAG.Core\SaddleRAG.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to solution and Mcp project reference**

```
dotnet sln SaddleRAG.slnx add SaddleRAG.Monitor/SaddleRAG.Monitor.csproj
```

Add to `SaddleRAG.Mcp/SaddleRAG.Mcp.csproj` inside the `<ItemGroup>` with other ProjectReferences:
```xml
<ProjectReference Include="..\SaddleRAG.Monitor\SaddleRAG.Monitor.csproj" />
```

- [ ] **Step 3: Create _Imports.razor**

```razor
@* SaddleRAG.Monitor/_Imports.razor *@
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Routing
@using MudBlazor
@using SaddleRAG.Core.Models.Monitor
@using SaddleRAG.Monitor.Theme
```

- [ ] **Step 4: Create the Wyoming theme**

```csharp
// SaddleRAG.Monitor/Theme/WyomingTheme.cs
// (standard file header)

#region Usings
using MudBlazor;
#endregion

namespace SaddleRAG.Monitor.Theme;

public static class WyomingTheme
{
    private const string BrownPrimary   = "#492F24";
    private const string GoldSecondary  = "#FFC425";
    private const string CreamBg        = "#FDF8F3";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary          = BrownPrimary,
            Secondary        = GoldSecondary,
            Background       = CreamBg,
            AppbarBackground = BrownPrimary,
            AppbarText       = Colors.Shades.White
        },
        PaletteDark = new PaletteDark
        {
            Primary   = GoldSecondary,
            Secondary = BrownPrimary
        }
    };
}
```

- [ ] **Step 5: Build to verify the new project compiles**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors.

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(monitor): scaffold SaddleRAG.Monitor Razor Class Library with Wyoming theme
```
```
git add SaddleRAG.Monitor SaddleRAG.Mcp/SaddleRAG.Mcp.csproj SaddleRAG.slnx
git commit -F msg.txt
```

---

## Task 10: Wire Blazor Server + SignalR into SaddleRAG.Mcp

**Files:**
- Modify: `SaddleRAG.Mcp/Program.cs`
- Create: `SaddleRAG.Mcp/Monitor/App.razor`
- Create: `SaddleRAG.Mcp/Monitor/Routes.razor`
- Create: `SaddleRAG.Mcp/_Host.cshtml` (or minimal Razor page entry point)

The MCP host is a plain ASP.NET Core app — not an MVC app. Blazor Server in .NET 10 can be added without MVC by calling `AddRazorComponents` + `AddServerSideBlazor`.

- [ ] **Step 1: Add Blazor and SignalR services to Program.cs**

In `Program.cs`, after the `AddMcpServer(...).WithHttpTransport(...)` chain, add:

```csharp
// Blazor Server + SignalR for /monitor
builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
builder.Services.AddSignalR();
```

Also add the MudBlazor services:
```csharp
builder.Services.AddMudServices();
```

Add to the usings region:
```csharp
using SaddleRAG.Monitor.Theme;
```

- [ ] **Step 2: Map Blazor and SignalR endpoints**

In `Program.cs`, in the `app.Map*` section (after the health check and MCP endpoint), add:

```csharp
// Blazor Server monitor
app.MapRazorComponents<SaddleRAG.Mcp.Monitor.App>()
   .AddInteractiveServerRenderMode();

// SignalR hub
app.MapHub<SaddleRAG.Mcp.Hubs.MonitorHub>("/monitor/hub");
```

- [ ] **Step 3: Create the Blazor app root component**

```razor
@* SaddleRAG.Mcp/Monitor/App.razor *@
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>SaddleRAG Monitor</title>
    <base href="/monitor/" />
    <link href="https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap" rel="stylesheet" />
    <link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <MudThemeProvider Theme="@WyomingTheme.Create()" />
    <MudSnackbarProvider />
    <MudDialogProvider />
    <Routes @rendermode="InteractiveServer" />
    <script src="_framework/blazor.server.js"></script>
    <script src="_content/MudBlazor/MudBlazor.min.js"></script>
</body>
</html>
```

- [ ] **Step 4: Create Routes.razor**

```razor
@* SaddleRAG.Mcp/Monitor/Routes.razor *@
<Router AppAssembly="@typeof(App).Assembly"
        AdditionalAssemblies="@(new[] { typeof(SaddleRAG.Monitor.Theme.WyomingTheme).Assembly })">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" />
    </Found>
    <NotFound>
        <MudText Typo="Typo.h6">Page not found.</MudText>
    </NotFound>
</Router>
```

- [ ] **Step 5: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors (MonitorHub doesn't exist yet — that's Task 11, so if the build references it comment it out temporarily until Task 11).

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(monitor): wire Blazor Server and SignalR into SaddleRAG.Mcp host
```
```
git add SaddleRAG.Mcp/Program.cs SaddleRAG.Mcp/Monitor
git commit -F msg.txt
```

---

## Task 11: MonitorHub SignalR hub + tick timer

**Files:**
- Create: `SaddleRAG.Mcp/Hubs/MonitorHub.cs`
- Create: `SaddleRAG.Mcp/Hubs/MonitorTickService.cs` (IHostedService driving 750 ms ticks)

- [ ] **Step 1: Create MonitorHub**

```csharp
// SaddleRAG.Mcp/Hubs/MonitorHub.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.SignalR;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Diagnostics;
#endregion

namespace SaddleRAG.Mcp.Hubs;

public sealed class MonitorHub : Hub
{
    public MonitorHub(IMonitorBroadcaster broadcaster)
    {
        mBroadcaster = broadcaster;
    }

    private readonly IMonitorBroadcaster mBroadcaster;

    /// <summary>
    ///     Subscribe to tick events for a specific job. Called by the job-detail page.
    /// </summary>
    public async Task SubscribeJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));
        // Send the current snapshot immediately so the page doesn't wait for the first tick.
        var snapshot = mBroadcaster.GetJobSnapshot(jobId);
        if (snapshot is not null)
            await Clients.Caller.SendAsync("JobTick", BuildTick(jobId, snapshot));
    }

    /// <summary>
    ///     Subscribe to landing-page coarse updates (active job list + aggregate counters).
    /// </summary>
    public async Task SubscribeLanding()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LandingGroup);
    }

    public static string GroupName(string jobId) => $"job-{jobId}";
    public const string LandingGroup = "landing";

    private static JobTickEvent BuildTick(string jobId, JobTickSnapshot snap) => new()
    {
        JobId          = jobId,
        At             = DateTime.UtcNow,
        Counters       = snap.Counters,
        CurrentHost    = snap.CurrentHost,
        RecentFetches  = snap.RecentFetches,
        RecentRejects  = snap.RecentRejects,
        ErrorsThisTick = snap.RecentErrors
    };
}
```

- [ ] **Step 2: Create MonitorTickService**

```csharp
// SaddleRAG.Mcp/Hubs/MonitorTickService.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.SignalR;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Mcp.Hubs;

/// <summary>
///     Background service that pushes 750 ms tick events to SignalR groups
///     for each active job, and landing-page heartbeats.
/// </summary>
public sealed class MonitorTickService : BackgroundService
{
    public MonitorTickService(IMonitorBroadcaster broadcaster,
                              IHubContext<MonitorHub> hub)
    {
        mBroadcaster = broadcaster;
        mHub         = hub;
    }

    private readonly IMonitorBroadcaster      mBroadcaster;
    private readonly IHubContext<MonitorHub>  mHub;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(750);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PushTicksAsync(stoppingToken);
    }

    private async Task PushTicksAsync(CancellationToken ct)
    {
        foreach (var jobId in mBroadcaster.GetActiveJobIds())
        {
            var snapshot = mBroadcaster.GetJobSnapshot(jobId);
            if (snapshot is null) continue;

            var tick = new JobTickEvent
            {
                JobId          = jobId,
                At             = DateTime.UtcNow,
                Counters       = snapshot.Counters,
                CurrentHost    = snapshot.CurrentHost,
                RecentFetches  = snapshot.RecentFetches,
                RecentRejects  = snapshot.RecentRejects,
                ErrorsThisTick = snapshot.RecentErrors
            };

            await mHub.Clients.Group(MonitorHub.GroupName(jobId))
                              .SendAsync("JobTick", tick, cancellationToken: ct);
        }

        // Landing-page heartbeat — just the active job ids for now
        var activeIds = mBroadcaster.GetActiveJobIds();
        await mHub.Clients.Group(MonitorHub.LandingGroup)
                          .SendAsync("ActiveJobs", activeIds, cancellationToken: ct);
    }
}
```

- [ ] **Step 3: Register MonitorTickService in Program.cs**

```csharp
builder.Services.AddHostedService<SaddleRAG.Mcp.Hubs.MonitorTickService>();
```

- [ ] **Step 4: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors.

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(monitor): add MonitorHub SignalR hub and 750ms MonitorTickService
```
```
git add SaddleRAG.Mcp/Hubs SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Phase D — UI Pages

---

## Task 12: Landing page (/monitor)

**Files:**
- Create: `SaddleRAG.Monitor/Pages/LandingPage.razor`
- Create: `SaddleRAG.Monitor/Pages/LandingPage.razor.cs`
- Create: `SaddleRAG.Monitor/Components/JobCardStrip.razor`
- Create: `SaddleRAG.Monitor/Components/LibraryCard.razor`

The landing page shows active jobs at top and a library card grid below. It subscribes to the SignalR `landing` group for `ActiveJobs` heartbeats and refreshes the library grid from the SaddleRAG API.

- [ ] **Step 1: Create the landing page**

```razor
@* SaddleRAG.Monitor/Pages/LandingPage.razor *@
@page "/monitor"
@rendermode InteractiveServer
@using SaddleRAG.Monitor.Components
@inherits LandingPageBase

<MudText Typo="Typo.h4" Class="mb-4">SaddleRAG Monitor</MudText>

@if (ActiveJobSnapshots.Count > 0)
{
    <MudText Typo="Typo.h6" Class="mb-2">Active Jobs</MudText>
    @foreach (var job in ActiveJobSnapshots)
    {
        <JobCardStrip Job="@job"
                      OnCancel="@(() => CancelJob(job.JobId))" />
    }
    <MudDivider Class="my-4" />
}

<MudText Typo="Typo.h6" Class="mb-2">Libraries</MudText>
@if (Libraries.Count == 0 && ActiveJobSnapshots.Count == 0)
{
    <MudCard>
        <MudCardContent>
            <MudText>No libraries indexed yet. Run <code>start_ingest</code> via Claude Code to get started.</MudText>
        </MudCardContent>
    </MudCard>
}
else
{
    <MudGrid>
        @foreach (var lib in Libraries)
        {
            <MudItem xs="12" sm="6" md="4">
                <LibraryCard Library="@lib" />
            </MudItem>
        }
    </MudGrid>
}
```

- [ ] **Step 2: Create the code-behind**

```csharp
// SaddleRAG.Monitor/Pages/LandingPage.razor.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LandingPageBase : ComponentBase, IAsyncDisposable
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected List<JobTickSnapshot>     ActiveJobSnapshots { get; } = [];
    protected List<LibrarySummaryItem>  Libraries          { get; } = [];

    private HubConnection? mHub;

    protected override async Task OnInitializedAsync()
    {
        mHub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri("/monitor/hub"))
            .WithAutomaticReconnect()
            .Build();

        mHub.On<IReadOnlyList<string>>("ActiveJobs", async ids =>
        {
            // Update the active jobs strip — simplified: re-fetch snapshots
            // A full implementation calls back to the server for each id's snapshot.
            await InvokeAsync(StateHasChanged);
        });

        await mHub.StartAsync();
        await mHub.InvokeAsync("SubscribeLanding");

        // Initial library load from server-side service (injected via cascading DI)
        // Full wiring done in Task 14 when library detail page adds the service layer.
    }

    protected async Task CancelJob(string jobId)
    {
        await Task.CompletedTask; // wired to write API in Task 16
    }

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }
}

/// <summary>
///     Minimal summary shown in library card grid.
/// </summary>
public sealed record LibrarySummaryItem
{
    public required string  LibraryId    { get; init; }
    public required string  Version      { get; init; }
    public required int     ChunkCount   { get; init; }
    public required int     PageCount    { get; init; }
    public required bool    IsSuspect    { get; init; }
    public string?          Hint         { get; init; }
}
```

- [ ] **Step 3: Create JobCardStrip component**

```razor
@* SaddleRAG.Monitor/Components/JobCardStrip.razor *@
@using SaddleRAG.Core.Models.Monitor

<MudCard Class="mb-2">
    <MudCardContent>
        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
            <MudText Typo="Typo.subtitle1">@Job.JobId</MudText>
            <MudProgressLinear Value="@ProgressPct" Color="Color.Primary" Class="flex-1" />
            <MudText Typo="Typo.body2">
                @Job.Counters.PagesCompleted / @Job.Counters.PagesQueued pages
            </MudText>
            <MudButton Size="Size.Small" Color="Color.Error" OnClick="OnCancel">Cancel</MudButton>
        </MudStack>
        @if (!string.IsNullOrEmpty(Job.CurrentHost))
        {
            <MudText Typo="Typo.caption" Class="mt-1">Current host: @Job.CurrentHost</MudText>
        }
    </MudCardContent>
</MudCard>

@code {
    [Parameter, EditorRequired] public JobTickSnapshot Job    { get; set; } = default!;
    [Parameter]                 public EventCallback   OnCancel { get; set; }

    private double ProgressPct => Job.Counters.PagesQueued == 0
        ? 0
        : (double) Job.Counters.PagesCompleted / Job.Counters.PagesQueued * 100.0;
}
```

- [ ] **Step 4: Create LibraryCard component**

```razor
@* SaddleRAG.Monitor/Components/LibraryCard.razor *@

<MudCard @onclick="@Navigate" Style="cursor:pointer">
    <MudCardContent>
        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
            <MudText Typo="Typo.subtitle1">@Library.LibraryId</MudText>
            <MudChip Size="Size.Small" Color="@(Library.IsSuspect ? Color.Warning : Color.Success)">
                @(Library.IsSuspect ? "⚠ suspect" : "✓ healthy")
            </MudChip>
        </MudStack>
        <MudText Typo="Typo.caption">v@Library.Version · @Library.ChunkCount chunks · @Library.PageCount pages</MudText>
        @if (!string.IsNullOrEmpty(Library.Hint))
        {
            <MudText Typo="Typo.body2" Class="mt-1">@Library.Hint</MudText>
        }
    </MudCardContent>
</MudCard>

@code {
    [Parameter, EditorRequired] public LibrarySummaryItem Library { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private void Navigate() => Nav.NavigateTo($"/monitor/libraries/{Library.LibraryId}");
}
```

- [ ] **Step 5: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors.

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(monitor): add landing page with active jobs strip and library card grid
```
```
git add SaddleRAG.Monitor/Pages/LandingPage.razor SaddleRAG.Monitor/Pages/LandingPage.razor.cs SaddleRAG.Monitor/Components/JobCardStrip.razor SaddleRAG.Monitor/Components/LibraryCard.razor
git commit -F msg.txt
```

---

## Task 13: Job detail page (/monitor/jobs/{id})

**Files:**
- Create: `SaddleRAG.Monitor/Pages/JobDetailPage.razor`
- Create: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs`
- Create: `SaddleRAG.Monitor/Components/PipelineStrip.razor`

- [ ] **Step 1: Create PipelineStrip component**

```razor
@* SaddleRAG.Monitor/Components/PipelineStrip.razor *@
@using SaddleRAG.Core.Models.Monitor

<MudStack Row="true" Spacing="2" Class="my-3">
    <PipelineCell Label="Crawl"   Count="@Counters.PagesFetched"    />
    <PipelineCell Label="Classify" Count="@Counters.PagesClassified" />
    <PipelineCell Label="Chunk"   Count="@Counters.ChunksGenerated"  />
    <PipelineCell Label="Embed"   Count="@Counters.ChunksEmbedded"   />
    <PipelineCell Label="Index"   Count="@Counters.PagesCompleted"   />
</MudStack>

@code {
    [Parameter, EditorRequired] public PipelineCounters Counters { get; set; } = default!;

    private sealed class PipelineCell : ComponentBase
    {
        // Inline sub-component to avoid a separate file for a trivial chip.
        // Rendered below via RenderFragment.
    }
}
```

Add a simple cell fragment inline:
```razor
@* Append to PipelineStrip.razor after the @code block *@
@* Cell rendered as a MudPaper per stage *@
```

Actually, keep it simple — render stages directly in PipelineStrip:
```razor
@* SaddleRAG.Monitor/Components/PipelineStrip.razor *@
@using SaddleRAG.Core.Models.Monitor

<MudStack Row="true" Spacing="2" Class="my-3" Wrap="Wrap.Wrap">
    @foreach (var stage in _stages)
    {
        <MudPaper Elevation="1" Class="pa-2" Style="min-width:110px;text-align:center">
            <MudText Typo="Typo.caption">@stage.Label</MudText>
            <MudText Typo="Typo.h6">@stage.Count</MudText>
        </MudPaper>
    }
</MudStack>

@code {
    [Parameter, EditorRequired] public PipelineCounters Counters { get; set; } = default!;

    private IReadOnlyList<(string Label, int Count)> _stages =>
    [
        ("Crawl",    Counters.PagesFetched),
        ("Classify", Counters.PagesClassified),
        ("Chunk",    Counters.ChunksGenerated),
        ("Embed",    Counters.ChunksEmbedded),
        ("Index",    Counters.PagesCompleted)
    ];
}
```

- [ ] **Step 2: Create JobDetailPage.razor**

```razor
@* SaddleRAG.Monitor/Pages/JobDetailPage.razor *@
@page "/monitor/jobs/{JobId}"
@rendermode InteractiveServer
@using SaddleRAG.Monitor.Components
@inherits JobDetailPageBase

<MudText Typo="Typo.h5">Job: @JobId</MudText>

@if (CurrentTick is not null)
{
    <PipelineStrip Counters="@CurrentTick.Counters" />

    <MudGrid Class="mt-2">
        <MudItem xs="12" md="6">
            <MudText Typo="Typo.subtitle2" Class="mb-1">Recent Fetches</MudText>
            <MudList Dense="true" T="string">
                @foreach (var f in CurrentTick.RecentFetches)
                {
                    <MudListItem>
                        <MudText Style="font-family:monospace;font-size:0.75rem">@f.Url</MudText>
                    </MudListItem>
                }
            </MudList>
        </MudItem>
        <MudItem xs="12" md="6">
            <MudText Typo="Typo.subtitle2" Class="mb-1">Recent Rejects</MudText>
            <MudList Dense="true" T="string">
                @foreach (var r in CurrentTick.RecentRejects)
                {
                    <MudListItem>
                        <MudStack Row="true">
                            <MudText Style="font-family:monospace;font-size:0.75rem">@r.Url</MudText>
                            <MudChip Size="Size.Small">@r.Reason</MudChip>
                        </MudStack>
                    </MudListItem>
                }
            </MudList>
        </MudItem>
    </MudGrid>

    @if (CurrentTick.ErrorsThisTick.Count > 0)
    {
        <MudAlert Severity="Severity.Error" Class="mt-3">
            @CurrentTick.ErrorsThisTick.Count error(s). Last: @CurrentTick.ErrorsThisTick[^1].Message
        </MudAlert>
    }
}
else
{
    <MudText>Waiting for first tick…</MudText>
}
```

- [ ] **Step 3: Create code-behind**

```csharp
// SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class JobDetailPageBase : ComponentBase, IAsyncDisposable
{
    [Parameter] public string JobId { get; set; } = string.Empty;
    [Inject]    private NavigationManager Nav { get; set; } = default!;

    protected JobTickEvent? CurrentTick { get; private set; }

    private HubConnection? mHub;

    protected override async Task OnInitializedAsync()
    {
        mHub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri("/monitor/hub"))
            .WithAutomaticReconnect()
            .Build();

        mHub.On<JobTickEvent>("JobTick", async tick =>
        {
            CurrentTick = tick;
            await InvokeAsync(StateHasChanged);
        });

        mHub.Closed += async _ =>
            await InvokeAsync(StateHasChanged);   // triggers disconnect UI in Task 18

        await mHub.StartAsync();
        await mHub.InvokeAsync("SubscribeJob", JobId);
    }

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }
}
```

- [ ] **Step 4: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(monitor): add job detail page with pipeline strip and dual URL feeds
```
```
git add SaddleRAG.Monitor/Pages/JobDetailPage.razor SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs SaddleRAG.Monitor/Components/PipelineStrip.razor
git commit -F msg.txt
```

---

## Task 14: Library detail page (/monitor/libraries/{id})

**Files:**
- Create: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor`
- Create: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs`
- Create: `SaddleRAG.Monitor/Services/MonitorDataService.cs`
- Modify: `SaddleRAG.Mcp/Program.cs` (register MonitorDataService)

`MonitorDataService` is a thin server-side service that wraps the existing `IScrapeJobRepository`, `ILibraryRepository`, etc. — it is injected into Blazor components that need to query MongoDB.

- [ ] **Step 1: Create MonitorDataService**

```csharp
// SaddleRAG.Monitor/Services/MonitorDataService.cs
// (standard file header)

#region Usings
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Monitor.Pages;
#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Server-side data access service for the Blazor monitor pages.
///     Wraps existing repositories so Blazor components don't take
///     direct repository dependencies.
/// </summary>
public sealed class MonitorDataService
{
    public MonitorDataService(ILibraryRepository libraries,
                              IScrapeJobRepository scrapeJobs,
                              IScrapeAuditRepository auditRepo)
    {
        mLibraries = libraries;
        mScrapeJobs = scrapeJobs;
        mAuditRepo  = auditRepo;
    }

    private readonly ILibraryRepository     mLibraries;
    private readonly IScrapeJobRepository   mScrapeJobs;
    private readonly IScrapeAuditRepository mAuditRepo;

    public async Task<IReadOnlyList<LibrarySummaryItem>> GetLibrarySummariesAsync(CancellationToken ct = default)
    {
        var libs = await mLibraries.GetAllAsync(ct);
        return libs.Select(l => new LibrarySummaryItem
        {
            LibraryId  = l.Id,
            Version    = l.CurrentVersion ?? string.Empty,
            ChunkCount = l.ChunkCount,
            PageCount  = l.PageCount,
            IsSuspect  = l.IsSuspect,
            Hint       = l.Hint
        }).ToList();
    }

    public async Task<LibraryDetailData?> GetLibraryDetailAsync(string libraryId, CancellationToken ct = default)
    {
        var lib = await mLibraries.GetByIdAsync(libraryId, ct);
        if (lib is null) return null;
        return new LibraryDetailData
        {
            LibraryId  = lib.Id,
            Version    = lib.CurrentVersion ?? string.Empty,
            ChunkCount = lib.ChunkCount,
            PageCount  = lib.PageCount,
            IsSuspect  = lib.IsSuspect,
            Hint       = lib.Hint
        };
    }
}

public sealed record LibraryDetailData
{
    public required string LibraryId  { get; init; }
    public required string Version    { get; init; }
    public required int    ChunkCount { get; init; }
    public required int    PageCount  { get; init; }
    public required bool   IsSuspect  { get; init; }
    public string?         Hint       { get; init; }
}
```

Note: the exact method names on `ILibraryRepository` (e.g. `GetAllAsync`, `GetByIdAsync`) may differ — grep for them:
```
grep -n "Task.*Library" SaddleRAG.Core/Interfaces/ILibraryRepository.cs
```
Adapt the calls to match.

- [ ] **Step 2: Register MonitorDataService in Program.cs**

```csharp
builder.Services.AddSingleton<SaddleRAG.Monitor.Services.MonitorDataService>();
```

- [ ] **Step 3: Create LibraryDetailPage.razor**

```razor
@* SaddleRAG.Monitor/Pages/LibraryDetailPage.razor *@
@page "/monitor/libraries/{LibraryId}"
@rendermode InteractiveServer
@inherits LibraryDetailPageBase

@if (Detail is null)
{
    <MudProgressCircular Indeterminate="true" />
}
else
{
    <MudText Typo="Typo.h5" Class="mb-1">@Detail.LibraryId</MudText>
    <MudText Typo="Typo.subtitle1" Class="mb-3">v@Detail.Version</MudText>

    <MudTabs Elevation="1" Rounded="true" ApplyEffectsToContainer="true">
        <MudTabPanel Text="Overview">
            <MudText>Chunks: @Detail.ChunkCount · Pages: @Detail.PageCount</MudText>
            @if (Detail.IsSuspect)
            {
                <MudAlert Severity="Severity.Warning" Class="mt-2">This library is flagged suspect.</MudAlert>
            }
        </MudTabPanel>
        <MudTabPanel Text="Audit">
            <MudButton Variant="Variant.Text"
                       Href="@($"/monitor/audits/{LatestJobId}")"
                       Disabled="@string.IsNullOrEmpty(LatestJobId)">
                View Full Audit
            </MudButton>
        </MudTabPanel>
        <MudTabPanel Text="Versions">
            <MudText>Version history coming soon.</MudText>
        </MudTabPanel>
    </MudTabs>
}
```

- [ ] **Step 4: Create code-behind**

```csharp
// SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Components;
using SaddleRAG.Monitor.Services;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LibraryDetailPageBase : ComponentBase
{
    [Parameter] public string LibraryId { get; set; } = string.Empty;
    [Inject] private MonitorDataService DataService { get; set; } = default!;

    protected LibraryDetailData? Detail      { get; private set; }
    protected string?            LatestJobId { get; private set; }

    protected override async Task OnParametersSetAsync()
    {
        Detail = await DataService.GetLibraryDetailAsync(LibraryId);
        // LatestJobId populated in Task 16 when write-endpoint wiring is complete.
    }
}
```

- [ ] **Step 5: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 6: Commit**

Write `msg.txt`:
```
feat(monitor): add library detail page with tabbed layout and MonitorDataService
```
```
git add SaddleRAG.Monitor/Pages/LibraryDetailPage.razor SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs SaddleRAG.Monitor/Services/MonitorDataService.cs SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Task 15: Audit inspector page (/monitor/audits/{jobId})

**Files:**
- Create: `SaddleRAG.Monitor/Pages/AuditInspectorPage.razor`
- Create: `SaddleRAG.Monitor/Pages/AuditInspectorPage.razor.cs`

- [ ] **Step 1: Create AuditInspectorPage.razor**

```razor
@* SaddleRAG.Monitor/Pages/AuditInspectorPage.razor *@
@page "/monitor/audits/{JobId}"
@rendermode InteractiveServer
@inherits AuditInspectorPageBase

<MudText Typo="Typo.h5" Class="mb-3">Audit — @JobId</MudText>

<MudStack Row="true" Spacing="2" Class="mb-3" Wrap="Wrap.Wrap">
    <MudSelect T="string" Label="Status" @bind-Value="FilterStatus" Clearable="true"
               Style="min-width:140px">
        @foreach (var s in _statusOptions)
        { <MudSelectItem Value="@s">@s</MudSelectItem> }
    </MudSelect>
    <MudSelect T="string" Label="Skip Reason" @bind-Value="FilterSkipReason" Clearable="true"
               Style="min-width:160px">
        @foreach (var r in _reasonOptions)
        { <MudSelectItem Value="@r">@r</MudSelectItem> }
    </MudSelect>
    <MudTextField T="string" Label="Host"        @bind-Value="FilterHost"       Immediate="true" />
    <MudTextField T="string" Label="URL contains" @bind-Value="FilterUrl"        Immediate="true" />
    <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="ApplyFilters">Search</MudButton>
</MudStack>

@if (Summary is not null)
{
    <MudStack Row="true" Spacing="2" Class="mb-3" Wrap="Wrap.Wrap">
        <MudChip>Considered: @Summary.TotalConsidered</MudChip>
        <MudChip Color="Color.Success">Indexed: @Summary.IndexedCount</MudChip>
        <MudChip Color="Color.Warning">Skipped: @Summary.SkippedCount</MudChip>
        <MudChip Color="Color.Error">Failed: @Summary.FailedCount</MudChip>
    </MudStack>
}

<MudTable Items="@Entries" Dense="true" Hover="true" LoadingProgressColor="Color.Primary">
    <HeaderContent>
        <MudTh>URL</MudTh><MudTh>Host</MudTh><MudTh>Depth</MudTh><MudTh>Status</MudTh><MudTh>Reason</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd Style="font-family:monospace;font-size:0.72rem;max-width:480px;overflow:hidden;text-overflow:ellipsis">
            @context.Url
        </MudTd>
        <MudTd>@context.Host</MudTd>
        <MudTd>@context.Depth</MudTd>
        <MudTd>
            <MudChip Size="Size.Small" Color="@StatusColor(context.Status.ToString())">
                @context.Status
            </MudChip>
        </MudTd>
        <MudTd>@context.SkipReason</MudTd>
    </RowTemplate>
    <NoRecordsContent>
        <MudText>No entries match the current filters.</MudText>
    </NoRecordsContent>
</MudTable>
```

- [ ] **Step 2: Create code-behind**

```csharp
// SaddleRAG.Monitor/Pages/AuditInspectorPage.razor.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Components;
using MudBlazor;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class AuditInspectorPageBase : ComponentBase
{
    [Parameter] public string JobId { get; set; } = string.Empty;
    [Inject] private IScrapeAuditRepository AuditRepo { get; set; } = default!;

    protected AuditSummary?                   Summary         { get; private set; }
    protected IReadOnlyList<ScrapeAuditLogEntry> Entries       { get; private set; } = [];

    protected string? FilterStatus     { get; set; }
    protected string? FilterSkipReason { get; set; }
    protected string? FilterHost       { get; set; }
    protected string? FilterUrl        { get; set; }

    private static readonly string[] _statusOptions =
        Enum.GetNames<AuditStatus>();
    private static readonly string[] _reasonOptions =
        Enum.GetNames<AuditSkipReason>();

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
    }

    protected async Task ApplyFilters()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Summary = await AuditRepo.SummarizeAsync(JobId);

        AuditStatus?     status = ParseEnum<AuditStatus>(FilterStatus);
        AuditSkipReason? reason = ParseEnum<AuditSkipReason>(FilterSkipReason);

        Entries = await AuditRepo.QueryAsync(JobId, status, reason,
                                             FilterHost, FilterUrl, limit: 200);
    }

    protected static Color StatusColor(string status) => status switch
    {
        "Indexed"  => Color.Success,
        "Fetched"  => Color.Info,
        "Skipped"  => Color.Warning,
        "Failed"   => Color.Error,
        _          => Color.Default
    };

    private static T? ParseEnum<T>(string? raw) where T : struct, Enum
    {
        T? result = null;
        if (!string.IsNullOrEmpty(raw) && Enum.TryParse<T>(raw, ignoreCase: true, out var v))
            result = v;
        return result;
    }
}
```

- [ ] **Step 3: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
feat(monitor): add audit inspector page with filter chips and paged URL table
```
```
git add SaddleRAG.Monitor/Pages/AuditInspectorPage.razor SaddleRAG.Monitor/Pages/AuditInspectorPage.razor.cs
git commit -F msg.txt
```

---

## Phase E — Auth + Write Endpoints

---

## Task 16: DiagnosticsWrite auth policy + write API endpoints

**Files:**
- Create: `SaddleRAG.Mcp/Auth/DiagnosticsWriteHandler.cs`
- Create: `SaddleRAG.Mcp/Api/MonitorApiEndpoints.cs`
- Modify: `SaddleRAG.Mcp/Program.cs`

- [ ] **Step 1: Create the auth policy handler**

```csharp
// SaddleRAG.Mcp/Auth/DiagnosticsWriteHandler.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Authorization;
#endregion

namespace SaddleRAG.Mcp.Auth;

public sealed class DiagnosticsWriteRequirement : IAuthorizationRequirement { }

public sealed class DiagnosticsWriteHandler : AuthorizationHandler<DiagnosticsWriteRequirement>
{
    public DiagnosticsWriteHandler(IConfiguration configuration, ILogger<DiagnosticsWriteHandler> logger)
    {
        mToken  = configuration["Diagnostics:WriteToken"];
        mLogger = logger;
    }

    private readonly string? mToken;
    private readonly ILogger<DiagnosticsWriteHandler> mLogger;
    private const string DiagnosticsWritePolicy = "DiagnosticsWrite";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                    DiagnosticsWriteRequirement requirement)
    {
        if (string.IsNullOrEmpty(mToken))
        {
            // Open mode — log once at startup (done in Program.cs); succeed here.
            context.Succeed(requirement);
        }
        else if (context.Resource is HttpContext http)
        {
            var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
            var bearer = $"Bearer {mToken}";
            if (string.Equals(authHeader, bearer, StringComparison.Ordinal))
                context.Succeed(requirement);
            else
                context.Fail();
        }
        else
        {
            context.Fail();
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Create write API endpoints**

```csharp
// SaddleRAG.Mcp/Api/MonitorApiEndpoints.cs
// (standard file header)

#region Usings
using Microsoft.AspNetCore.Authorization;
using SaddleRAG.Core.Interfaces;
#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorApiEndpoints
{
    private const string DiagnosticsWritePolicy = "DiagnosticsWrite";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/monitor")
                       .RequireAuthorization(DiagnosticsWritePolicy);

        group.MapPost("/jobs/{jobId}/cancel", CancelJob);
        group.MapPost("/libraries/{libraryId}/rescrape", RescrapeLibrary);
        group.MapPost("/libraries/{libraryId}/rescrub",  RescrubLibrary);
    }

    private static async Task<IResult> CancelJob(string jobId,
                                                   IScrapeJobQueue queue)
    {
        // IScrapeJobQueue.CancelAsync — the existing cancel mechanism
        // Grep: grep -n "CancelAsync\|Cancel" SaddleRAG.Core/Interfaces/IScrapeJobQueue.cs
        await queue.CancelJobAsync(jobId);
        return Results.Ok(new { JobId = jobId, Status = "CancelRequested" });
    }

    private static IResult RescrapeLibrary(string libraryId)
    {
        // Full implementation deferred — returns 501 until wired in a follow-up.
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    private static IResult RescrubLibrary(string libraryId)
    {
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }
}
```

Note: The exact method name on `IScrapeJobQueue` for cancellation may differ. Grep to find it:
```
grep -n "Cancel" SaddleRAG.Core/Interfaces/IScrapeJobQueue.cs
```
Adapt the call accordingly.

- [ ] **Step 3: Wire auth and endpoints into Program.cs**

Add service registrations (before `builder.Build()`):
```csharp
// Diagnostics write auth policy
builder.Services.AddAuthorization(opts =>
    opts.AddPolicy("DiagnosticsWrite",
        policy => policy.AddRequirements(new SaddleRAG.Mcp.Auth.DiagnosticsWriteRequirement())));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
                               SaddleRAG.Mcp.Auth.DiagnosticsWriteHandler>();
```

After `var app = builder.Build();`, add warning log if token is unset:
```csharp
var writeToken = app.Configuration["Diagnostics:WriteToken"];
if (string.IsNullOrEmpty(writeToken))
    app.Logger.LogWarning("Diagnostics:WriteToken is unset — write endpoints are open. " +
                          "Set the token before exposing the monitor beyond localhost.");
```

After the health and MCP endpoint mappings, add:
```csharp
SaddleRAG.Mcp.Api.MonitorApiEndpoints.Map(app);
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 4: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(monitor): add DiagnosticsWrite auth policy and /api/monitor write endpoints
```
```
git add SaddleRAG.Mcp/Auth SaddleRAG.Mcp/Api/MonitorApiEndpoints.cs SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Task 17: Wire Blazor Cancel/Pause buttons to write API

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/LandingPage.razor.cs` (implement CancelJob)
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs` (add cancel button)
- Create: `SaddleRAG.Monitor/Services/MonitorWriteService.cs`

- [ ] **Step 1: Create MonitorWriteService**

```csharp
// SaddleRAG.Monitor/Services/MonitorWriteService.cs
// (standard file header)

#region Usings
using System.Net.Http.Json;
#endregion

namespace SaddleRAG.Monitor.Services;

public sealed class MonitorWriteService
{
    public MonitorWriteService(HttpClient http)
    {
        mHttp = http;
    }

    private readonly HttpClient mHttp;

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        var response = await mHttp.PostAsync($"/api/monitor/jobs/{jobId}/cancel",
                                              content: null, ct);
        return response.IsSuccessStatusCode;
    }
}
```

Register in Program.cs with the base address pointing to itself:
```csharp
builder.Services.AddHttpClient<SaddleRAG.Monitor.Services.MonitorWriteService>(
    client => client.BaseAddress = new Uri($"http://localhost:{port}/"));
```

Where `port` is retrieved from configuration:
```csharp
// Read configured port (default 6100 matching existing Kestrel config)
const int DefaultMonitorPort = 6100;
var port = builder.Configuration.GetValue<int?>("Kestrel:Endpoints:Http:Port") ?? DefaultMonitorPort;
```

- [ ] **Step 2: Update LandingPage CancelJob**

In `LandingPage.razor.cs`, inject `MonitorWriteService` and implement `CancelJob`:

```csharp
[Inject] private MonitorWriteService WriteService { get; set; } = default!;

protected async Task CancelJob(string jobId)
{
    await WriteService.CancelJobAsync(jobId);
}
```

- [ ] **Step 3: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 4: Commit**

Write `msg.txt`:
```
feat(monitor): wire Blazor Cancel buttons to /api/monitor write endpoints
```
```
git add SaddleRAG.Monitor/Services/MonitorWriteService.cs SaddleRAG.Monitor/Pages SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Phase F — Polish

---

## Task 18: Hub-disconnect fallback polling + empty/loading/error states

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor` + `.razor.cs`
- Create: `SaddleRAG.Mcp/Api/MonitorSnapshotEndpoints.cs` (GET snapshot endpoint)

When the SignalR hub disconnects, the job-detail page falls back to polling a JSON snapshot endpoint every 5 seconds, then resumes push when the hub reconnects.

- [ ] **Step 1: Add GET snapshot endpoint**

```csharp
// SaddleRAG.Mcp/Api/MonitorSnapshotEndpoints.cs
// (standard file header)

#region Usings
using SaddleRAG.Core.Interfaces;
#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorSnapshotEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitor/jobs/{jobId}/snapshot", GetSnapshot);
    }

    private static IResult GetSnapshot(string jobId, IMonitorBroadcaster broadcaster)
    {
        var snapshot = broadcaster.GetJobSnapshot(jobId);
        if (snapshot is null)
            return Results.NotFound(new { JobId = jobId, Status = "not_found" });
        return Results.Ok(snapshot);
    }
}
```

Call `MonitorSnapshotEndpoints.Map(app);` in `Program.cs` after `MonitorApiEndpoints.Map(app)`.

- [ ] **Step 2: Add hub-disconnect fallback in JobDetailPage.razor.cs**

Extend `JobDetailPageBase` with polling fallback:

```csharp
private PeriodicTimer?         mFallbackTimer;
private CancellationTokenSource mFallbackCts = new();
protected bool HubConnected { get; private set; } = true;
[Inject] private HttpClient Http { get; set; } = default!;

// In OnInitializedAsync, extend hub handlers:
mHub!.Closed += async _ =>
{
    HubConnected = false;
    await InvokeAsync(StateHasChanged);
    await StartFallbackPollingAsync();
};

mHub.Reconnected += async _ =>
{
    HubConnected = true;
    StopFallbackPolling();
    await InvokeAsync(StateHasChanged);
};

private async Task StartFallbackPollingAsync()
{
    mFallbackTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    try
    {
        while (await mFallbackTimer.WaitForNextTickAsync(mFallbackCts.Token))
        {
            var snapshot = await Http.GetFromJsonAsync<JobTickSnapshot>(
                $"/api/monitor/jobs/{JobId}/snapshot", mFallbackCts.Token);
            if (snapshot is not null)
            {
                CurrentTick = new JobTickEvent
                {
                    JobId          = JobId,
                    At             = DateTime.UtcNow,
                    Counters       = snapshot.Counters,
                    CurrentHost    = snapshot.CurrentHost,
                    RecentFetches  = snapshot.RecentFetches,
                    RecentRejects  = snapshot.RecentRejects,
                    ErrorsThisTick = snapshot.RecentErrors
                };
                await InvokeAsync(StateHasChanged);
            }
        }
    }
    catch (OperationCanceledException) { }
}

private void StopFallbackPolling()
{
    mFallbackCts.Cancel();
    mFallbackTimer?.Dispose();
    mFallbackTimer = null;
    mFallbackCts   = new CancellationTokenSource();
}

// In DisposeAsync, add:
// StopFallbackPolling(); mFallbackCts.Dispose();
```

- [ ] **Step 3: Add disconnect banner to JobDetailPage.razor**

```razor
@if (!HubConnected)
{
    <MudAlert Severity="Severity.Warning" Class="mb-2">
        Live updates paused — reconnecting… (polling every 5 s)
    </MudAlert>
}
```

- [ ] **Step 4: Build**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 5: Commit**

Write `msg.txt`:
```
feat(monitor): add hub-disconnect fallback polling and disconnect banner
```
```
git add SaddleRAG.Monitor/Pages/JobDetailPage.razor SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs SaddleRAG.Mcp/Api/MonitorSnapshotEndpoints.cs SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Phase G — Tests

---

## Task 19: MonitorBroadcaster unit test gap-fill

The unit tests written in Task 7 cover the basics. This task fills any gaps and adds isolated SignalR-tick verification.

**Files:**
- Modify: `SaddleRAG.Tests/Monitor/MonitorBroadcasterTests.cs`

- [ ] **Step 1: Add tests for concurrent safety and rolling buffer ordering**

```csharp
[Fact]
public void ConcurrentFetchesDoNotLoseCount()
{
    var broadcaster = new MonitorBroadcaster();
    broadcaster.RecordJobStarted("job-c", "lib", "1.0", "https://c.com/");

    Parallel.For(0, 100, i => broadcaster.RecordFetch("job-c", $"https://c.com/{i}"));

    var snapshot = broadcaster.GetJobSnapshot("job-c");
    Assert.NotNull(snapshot);
    Assert.Equal(100, snapshot!.Counters.PagesFetched);
}

[Fact]
public void RecentRejectsFeedCapsAt50()
{
    var broadcaster = new MonitorBroadcaster();
    broadcaster.RecordJobStarted("job-r", "lib", "1.0", "https://r.com/");

    for (var i = 0; i < 70; i++)
        broadcaster.RecordReject("job-r", $"https://r.com/{i}", "PatternExclude");

    var snapshot = broadcaster.GetJobSnapshot("job-r");
    Assert.Equal(50, snapshot!.RecentRejects.Count);
}

[Fact]
public void UnknownJobRecordFetchIsNoOp()
{
    var broadcaster = new MonitorBroadcaster();
    broadcaster.RecordFetch("no-such-job", "https://x.com/");
    Assert.Null(broadcaster.GetJobSnapshot("no-such-job"));
}
```

- [ ] **Step 2: Run all monitor tests**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorBroadcasterTests"
```
Expected: PASS.

- [ ] **Step 3: Commit**

Write `msg.txt`:
```
test(monitor): add concurrent safety and rolling buffer cap tests for MonitorBroadcaster
```
```
git add SaddleRAG.Tests/Monitor/MonitorBroadcasterTests.cs
git commit -F msg.txt
```

---

## Task 20: Final build verification + full test suite

- [ ] **Step 1: Run full build with warnings-as-errors**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: zero errors, zero warnings.

- [ ] **Step 2: Run full test suite**

```
dotnet test SaddleRAG.slnx
```
Expected: all green. Note any integration tests that require MongoDB — they will skip if Mongo is not running; that's expected.

- [ ] **Step 3: Smoke-test the monitor UI manually**

Start the MCP service in Debug mode. Navigate to `http://localhost:6100/monitor` in a browser.
- Confirm the landing page renders with the Wyoming brown/gold theme.
- Trigger a small scrape via `scrape_docs` in Claude Code.
- Navigate to the job detail page — confirm tick events render in the Recent Fetches list.
- Navigate to `/monitor/audits/{jobId}` after the scrape completes — confirm audit rows load.

- [ ] **Step 4: Commit any fixups from smoke testing**

If any CSS or rendering issues are found, fix them inline and commit with:
```
fix(monitor): address smoke-test UI issues
```

---

## Wave 2 Complete

After all tasks pass:
- Five deferred Wave 1 bugs are fixed (port normalization, channel race, in-memory aggregation, lineage data gaps).
- `MonitorBroadcaster` feeds real-time pipeline events to SignalR.
- The Blazor Server monitor at `/monitor` provides landing, job detail, library detail, and audit inspector pages.
- Write endpoints (cancel) are gated by the `DiagnosticsWrite` policy.
- Hub-disconnect fallback polling keeps the job-detail page live even when SignalR drops.
- Both Wave 1 and Wave 2 are ready to merge to master via a single PR.

## Self-review gaps addressed during plan writing

- **Port bug**: covered in Task 1 (cheap fix with a reflection-based unit test).
- **Channel race**: covered in Task 2 (SingleReader → false + deterministic test rewrite).
- **SummarizeAsync perf**: covered in Task 3 ($group pipeline, three parallel aggregations).
- **ParentUrl lineage**: covered in Tasks 4 and 5 (CrawlEntry + PageRecord + DocChunk chain).
- **MonitorBroadcaster**: Tasks 6–8.
- **Blazor RCL + theme**: Task 9.
- **SignalR + Blazor Server wiring**: Tasks 10–11.
- **Four UI pages**: Tasks 12–15.
- **Auth policy**: Task 16.
- **Write buttons**: Task 17.
- **Disconnect fallback**: Task 18.
- **Tests**: Tasks 7 (unit), 19 (gap-fill), 20 (smoke).

**Spec requirements with no task:** bUnit Blazor component tests and the Playwright E2E test (spec section "UI smoke tests") are deferred to a follow-up PR — they require the full monitor to be running first and add significant setup overhead. Mark as a known gap.
