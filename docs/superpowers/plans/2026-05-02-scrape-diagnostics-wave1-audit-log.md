# Scrape Diagnostics Wave 1 — Audit Log + Inspection Tool

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-URL audit log (`ScrapeAuditLog` MongoDB collection) populated during every scrape and dryrun, plus the `inspect_scrape` MCP tool that lets the LLM answer "should I run this?", "why was X excluded?", and "are my filters too tight?" — all without standing up any UI.

**Architecture:** A new buffered writer service (`ScrapeAuditWriter`) hooks into the three existing filter call sites in `PageCrawler` and the per-page completion path in `IngestionOrchestrator`. The writer batches inserts (500 rows or 1s) into a new `ScrapeAuditLog` collection. `inspect_scrape` is a static MCP tool class following existing patterns (`IngestTools.cs`, `SearchTools.cs`). `dryrun_scrape` becomes async (returns a jobId immediately) so its audit can be inspected the same way as a real scrape.

**Tech Stack:** C# / .NET 9, MongoDB.Driver, xUnit, ModelContextProtocol.AspNetCore. No new dependencies.

---

## Spec Reference

See `docs/superpowers/specs/2026-05-02-scrape-diagnostics-monitor-design.md`. This plan implements only the audit log + MCP tool surface. The Blazor monitor, SignalR hub, and UI pages are Wave 2.

## File Structure

**New files:**
- `SaddleRAG.Core/Models/Audit/ScrapeAuditLogEntry.cs` — record + nested `AuditPageOutcome`.
- `SaddleRAG.Core/Models/Audit/AuditStatus.cs` — enum.
- `SaddleRAG.Core/Models/Audit/AuditSkipReason.cs` — enum.
- `SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs` — repository contract.
- `SaddleRAG.Core/Interfaces/IScrapeAuditWriter.cs` — writer service contract (used by ingestion).
- `SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs` — MongoDB implementation.
- `SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs` — buffered batch writer.
- `SaddleRAG.Mcp/Tools/InspectScrapeTool.cs` — `inspect_scrape` MCP tool.
- `SaddleRAG.Tests/Audit/AuditEnumStabilityTests.cs`
- `SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs`
- `SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs`
- `SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs`
- `SaddleRAG.Tests/Audit/ScrapeAuditCleanupIntegrationTests.cs`
- `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`

**Modified files:**
- `SaddleRAG.Database/SaddleRagDbContext.cs` — add `ScrapeAuditLog` collection + indexes.
- `SaddleRAG.Database/Repositories/RepositoryFactory.cs` — expose `ScrapeAudit` repository.
- `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` — call audit writer at filter and outcome sites.
- `SaddleRAG.Ingestion/IngestionOrchestrator.cs` — call audit writer on indexed-page completion.
- `SaddleRAG.Mcp/Program.cs` (or wherever services are registered) — register `IScrapeAuditWriter`.
- `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` — extend cleanup path to clear audit.
- `SaddleRAG.Mcp/Tools/DryRunScrapeTool` (in whatever file currently hosts dryrun_scrape) — convert to async/queued.

The implementer should grep for `dryrun_scrape` and the existing scrape cleanup callsites to locate exact modification points.

---

## Conventions (apply to every new file)

1. **File header** (mandatory):
   ```csharp
   // FileName.cs
   // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
   // SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
   // Available under AGPLv3 (see LICENSE) or a commercial license
   // (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.
   ```
2. `#region Usings` block immediately under the namespace's using block.
3. Single namespace per file. Match folder structure.
4. Allman braces; 4-space indent; max 120 char lines.
5. Field prefixes: `m` (instance), `ps` (private static), `sm` (static readonly).
6. Tests: xUnit `[Fact]`, `Assert.*`, namespace mirrors source folder under `SaddleRAG.Tests`.

---

## Task 1: Audit log core types

**Files:**
- Create: `SaddleRAG.Core/Models/Audit/AuditStatus.cs`
- Create: `SaddleRAG.Core/Models/Audit/AuditSkipReason.cs`
- Create: `SaddleRAG.Core/Models/Audit/ScrapeAuditLogEntry.cs`
- Create: `SaddleRAG.Tests/Audit/AuditEnumStabilityTests.cs`

- [ ] **Step 1: Write the failing enum-stability test**

```csharp
// SaddleRAG.Tests/Audit/AuditEnumStabilityTests.cs
// (header omitted for brevity — apply standard header on every new file)

using SaddleRAG.Core.Models.Audit;

namespace SaddleRAG.Tests.Audit;

public sealed class AuditEnumStabilityTests
{
    [Fact]
    public void AuditStatusValuesArePinned()
    {
        Assert.Equal(0, (int) AuditStatus.Considered);
        Assert.Equal(1, (int) AuditStatus.Skipped);
        Assert.Equal(2, (int) AuditStatus.Fetched);
        Assert.Equal(3, (int) AuditStatus.Failed);
        Assert.Equal(4, (int) AuditStatus.Indexed);
    }

    [Fact]
    public void AuditSkipReasonValuesArePinned()
    {
        Assert.Equal(0, (int) AuditSkipReason.PatternExclude);
        Assert.Equal(1, (int) AuditSkipReason.PatternMissAllowed);
        Assert.Equal(2, (int) AuditSkipReason.BinaryExt);
        Assert.Equal(3, (int) AuditSkipReason.OffSiteDepth);
        Assert.Equal(4, (int) AuditSkipReason.SameHostDepth);
        Assert.Equal(5, (int) AuditSkipReason.HostGated);
        Assert.Equal(6, (int) AuditSkipReason.AlreadyVisited);
        Assert.Equal(7, (int) AuditSkipReason.QueueLimit);
    }

    [Fact]
    public void ScrapeAuditLogEntryRoundTripsThroughJson()
    {
        var entry = new ScrapeAuditLogEntry
        {
            Id = "abc-123",
            JobId = "job-1",
            LibraryId = "lib",
            Version = "1.0",
            Url = "https://example.com/a",
            ParentUrl = "https://example.com/",
            Host = "example.com",
            Depth = 1,
            DiscoveredAt = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
            Status = AuditStatus.Skipped,
            SkipReason = AuditSkipReason.OffSiteDepth,
            SkipDetail = "depth=2 limit=1",
            PageOutcome = null
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ScrapeAuditLogEntry>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(entry.Id, roundTrip!.Id);
        Assert.Equal(entry.Status, roundTrip.Status);
        Assert.Equal(entry.SkipReason, roundTrip.SkipReason);
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~AuditEnumStabilityTests"`
Expected: FAIL — `AuditStatus`, `AuditSkipReason`, `ScrapeAuditLogEntry` do not exist.

- [ ] **Step 3: Create the enum and record types**

```csharp
// SaddleRAG.Core/Models/Audit/AuditStatus.cs
namespace SaddleRAG.Core.Models.Audit;

public enum AuditStatus
{
    Considered = 0,
    Skipped    = 1,
    Fetched    = 2,
    Failed     = 3,
    Indexed    = 4
}
```

```csharp
// SaddleRAG.Core/Models/Audit/AuditSkipReason.cs
namespace SaddleRAG.Core.Models.Audit;

public enum AuditSkipReason
{
    PatternExclude     = 0,
    PatternMissAllowed = 1,
    BinaryExt          = 2,
    OffSiteDepth       = 3,
    SameHostDepth      = 4,
    HostGated          = 5,
    AlreadyVisited     = 6,
    QueueLimit         = 7
}
```

```csharp
// SaddleRAG.Core/Models/Audit/ScrapeAuditLogEntry.cs
namespace SaddleRAG.Core.Models.Audit;

public sealed class ScrapeAuditLogEntry
{
    public required string Id { get; init; }
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
    public string? SkipDetail { get; init; }

    public AuditPageOutcome? PageOutcome { get; init; }
}

public sealed class AuditPageOutcome
{
    public string? FetchStatus { get; init; }
    public string? Category { get; init; }
    public int? ChunkCount { get; init; }
    public string? Error { get; init; }
}
```

- [ ] **Step 4: Run the tests to verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~AuditEnumStabilityTests"`
Expected: PASS — three tests green.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Core/Models/Audit SaddleRAG.Tests/Audit/AuditEnumStabilityTests.cs
git commit -F msg.txt
```
where `msg.txt` contains:
```
feat(audit): add ScrapeAuditLogEntry and audit enums
```

---

## Task 2: DbContext audit collection + indexes

**Files:**
- Modify: `SaddleRAG.Database/SaddleRagDbContext.cs` (add collection accessor + indexes)
- Create: `SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs` (just the indexes-exist test for now)

This task is gated on a running test MongoDB instance. Existing tests use it the same way; mirror their fixture pattern. If the project has a `[Collection("Mongo")]` xUnit fixture, use it.

- [ ] **Step 1: Write the failing index-exists test**

```csharp
// SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs
using MongoDB.Driver;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;

namespace SaddleRAG.Tests.Audit;

[Collection("Mongo")]
public sealed class ScrapeAuditRepositoryTests
{
    public ScrapeAuditRepositoryTests(MongoFixture fixture)
    {
        mContext = fixture.NewContext();
    }

    private readonly SaddleRagDbContext mContext;

    [Fact]
    public async Task EnsureIndexesAsyncCreatesAuditIndexes()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var indexes = await (await mContext.ScrapeAuditLog.Indexes.ListAsync())
                              .ToListAsync();

        // Three compound indexes plus Mongo's implicit _id index = 4 total
        Assert.True(indexes.Count >= 4);
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Status_1"));
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Host_1"));
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Url_1"));
    }
}
```

(If the existing test fixture isn't named `MongoFixture` / `[Collection("Mongo")]`, follow the actual convention you find by reading any existing `*IntegrationTests.cs` file.)

- [ ] **Step 2: Run the test to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~EnsureIndexesAsyncCreatesAuditIndexes"`
Expected: FAIL — `mContext.ScrapeAuditLog` does not exist.

- [ ] **Step 3: Add collection accessor and indexes**

In `SaddleRAG.Database/SaddleRagDbContext.cs`, add the collection accessor near the existing `ScrapeJobs` accessor:

```csharp
using SaddleRAG.Core.Models.Audit;

// ... existing accessors ...

public IMongoCollection<ScrapeAuditLogEntry> ScrapeAuditLog =>
    mDatabase.GetCollection<ScrapeAuditLogEntry>(CollectionScrapeAuditLog);
```

Add the collection name constant alongside other `Collection*` constants:

```csharp
private const string CollectionScrapeAuditLog = "scrape_audit_log";
```

Add indexes inside `EnsureIndexesAsync`:

```csharp
// ScrapeAuditLog: bucketed query by status/skip-reason
var auditKeys = Builders<ScrapeAuditLogEntry>.IndexKeys;
await ScrapeAuditLog.Indexes.CreateOneAsync(
    new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(
        auditKeys.Ascending(a => a.JobId),
        auditKeys.Ascending(a => a.Status),
        auditKeys.Ascending(a => a.SkipReason)
    )),
    cancellationToken: ct);

// ScrapeAuditLog: by-host views
await ScrapeAuditLog.Indexes.CreateOneAsync(
    new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(
        auditKeys.Ascending(a => a.JobId),
        auditKeys.Ascending(a => a.Host)
    )),
    cancellationToken: ct);

// ScrapeAuditLog: single-URL forensics
await ScrapeAuditLog.Indexes.CreateOneAsync(
    new CreateIndexModel<ScrapeAuditLogEntry>(auditKeys.Combine(
        auditKeys.Ascending(a => a.JobId),
        auditKeys.Ascending(a => a.Url)
    )),
    cancellationToken: ct);
```

- [ ] **Step 4: Run the test to verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~EnsureIndexesAsyncCreatesAuditIndexes"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Database/SaddleRagDbContext.cs SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): add ScrapeAuditLog collection and indexes
```

---

## Task 3: Audit repository (interface + impl)

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs`
- Create: `SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs`
- Modify: `SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs` (add round-trip tests)

- [ ] **Step 1: Write the failing repository tests**

Add these tests to `ScrapeAuditRepositoryTests` (alongside the existing index test):

```csharp
[Fact]
public async Task InsertManyAndQueryByJobIdRoundTrips()
{
    await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
    var repo = new ScrapeAuditRepository(mContext);
    var jobId = $"test-{Guid.NewGuid():N}";

    var entries = new[]
    {
        MakeEntry(jobId, "https://a.com/1", AuditStatus.Indexed),
        MakeEntry(jobId, "https://a.com/2", AuditStatus.Skipped, AuditSkipReason.PatternExclude),
        MakeEntry(jobId, "https://b.com/1", AuditStatus.Fetched)
    };

    await repo.InsertManyAsync(entries, TestContext.Current.CancellationToken);

    var fetched = await repo.QueryAsync(jobId, status: null, skipReason: null,
                                        host: null, urlSubstring: null, limit: 100,
                                        TestContext.Current.CancellationToken);

    Assert.Equal(3, fetched.Count);
}

[Fact]
public async Task DeleteByLibraryVersionRemovesPriorAudit()
{
    var repo = new ScrapeAuditRepository(mContext);
    var jobId = $"test-{Guid.NewGuid():N}";

    await repo.InsertManyAsync(new[] { MakeEntry(jobId, "https://a.com/", AuditStatus.Indexed) },
                               TestContext.Current.CancellationToken);

    var removed = await repo.DeleteByLibraryVersionAsync("lib-x", "1.0",
                                                          TestContext.Current.CancellationToken);

    Assert.True(removed >= 1);
    var remaining = await repo.QueryAsync(jobId, null, null, null, null, 100,
                                          TestContext.Current.CancellationToken);
    Assert.Empty(remaining);
}

[Fact]
public async Task SummarizeReturnsBucketedCounts()
{
    var repo = new ScrapeAuditRepository(mContext);
    var jobId = $"test-{Guid.NewGuid():N}";

    var entries = new[]
    {
        MakeEntry(jobId, "https://a.com/1", AuditStatus.Indexed),
        MakeEntry(jobId, "https://a.com/2", AuditStatus.Indexed),
        MakeEntry(jobId, "https://a.com/3", AuditStatus.Skipped, AuditSkipReason.OffSiteDepth),
        MakeEntry(jobId, "https://a.com/4", AuditStatus.Skipped, AuditSkipReason.OffSiteDepth),
        MakeEntry(jobId, "https://a.com/5", AuditStatus.Skipped, AuditSkipReason.PatternExclude)
    };
    await repo.InsertManyAsync(entries, TestContext.Current.CancellationToken);

    var summary = await repo.SummarizeAsync(jobId, TestContext.Current.CancellationToken);

    Assert.Equal(2, summary.IndexedCount);
    Assert.Equal(3, summary.SkippedCount);
    Assert.Equal(2, summary.SkipReasonCounts[AuditSkipReason.OffSiteDepth]);
    Assert.Equal(1, summary.SkipReasonCounts[AuditSkipReason.PatternExclude]);
}

private static ScrapeAuditLogEntry MakeEntry(string jobId, string url, AuditStatus status,
                                              AuditSkipReason? skip = null)
{
    var uri = new Uri(url);
    return new ScrapeAuditLogEntry
    {
        Id = Guid.NewGuid().ToString("N"),
        JobId = jobId,
        LibraryId = "lib-x",
        Version = "1.0",
        Url = url,
        Host = uri.Host,
        Depth = 1,
        DiscoveredAt = DateTime.UtcNow,
        Status = status,
        SkipReason = skip
    };
}
```

- [ ] **Step 2: Run the tests to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditRepositoryTests"`
Expected: FAIL — `IScrapeAuditRepository` and `ScrapeAuditRepository` do not exist.

- [ ] **Step 3: Implement the repository**

```csharp
// SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs
using SaddleRAG.Core.Models.Audit;

namespace SaddleRAG.Core.Interfaces;

public interface IScrapeAuditRepository
{
    Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default);

    Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId,
                                                        AuditStatus? status,
                                                        AuditSkipReason? skipReason,
                                                        string? host,
                                                        string? urlSubstring,
                                                        int limit,
                                                        CancellationToken ct = default);

    Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default);

    Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default);

    Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default);

    Task<long> DeleteByLibraryVersionAsync(string libraryId, string version, CancellationToken ct = default);
}

public sealed class AuditSummary
{
    public required string JobId { get; init; }
    public required int TotalConsidered { get; init; }
    public required int IndexedCount { get; init; }
    public required int FetchedCount { get; init; }
    public required int FailedCount { get; init; }
    public required int SkippedCount { get; init; }
    public required IReadOnlyDictionary<AuditSkipReason, int> SkipReasonCounts { get; init; }
    public required IReadOnlyDictionary<string, int> HostCounts { get; init; }
}
```

```csharp
// SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs
#region Usings

using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Database.Repositories;

public sealed class ScrapeAuditRepository : IScrapeAuditRepository
{
    public ScrapeAuditRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    public async Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        if (list.Count == 0)
            return;
        await mContext.ScrapeAuditLog.InsertManyAsync(list,
                                                      new InsertManyOptions { IsOrdered = false },
                                                      ct);
    }

    public async Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId,
                                                                     AuditStatus? status,
                                                                     AuditSkipReason? skipReason,
                                                                     string? host,
                                                                     string? urlSubstring,
                                                                     int limit,
                                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var filter = Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.JobId, jobId);
        if (status.HasValue)
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Status, status.Value);
        if (skipReason.HasValue)
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.SkipReason, skipReason.Value);
        if (!string.IsNullOrEmpty(host))
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Host, host);
        if (!string.IsNullOrEmpty(urlSubstring))
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Regex(a => a.Url,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(urlSubstring)));

        return await mContext.ScrapeAuditLog.Find(filter)
                                            .Limit(limit > 0 ? limit : 50)
                                            .ToListAsync(ct);
    }

    public async Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default)
    {
        var filter = Builders<ScrapeAuditLogEntry>.Filter.And(
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.JobId, jobId),
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Url, url));
        return await mContext.ScrapeAuditLog.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
    {
        var entries = await QueryAsync(jobId, null, null, null, null, int.MaxValue, ct);

        var skipReasonCounts = entries.Where(e => e.SkipReason.HasValue)
                                       .GroupBy(e => e.SkipReason!.Value)
                                       .ToDictionary(g => g.Key, g => g.Count());

        var hostCounts = entries.GroupBy(e => e.Host)
                                .ToDictionary(g => g.Key, g => g.Count());

        return new AuditSummary
        {
            JobId = jobId,
            TotalConsidered = entries.Count,
            IndexedCount = entries.Count(e => e.Status == AuditStatus.Indexed),
            FetchedCount = entries.Count(e => e.Status == AuditStatus.Fetched),
            FailedCount = entries.Count(e => e.Status == AuditStatus.Failed),
            SkippedCount = entries.Count(e => e.Status == AuditStatus.Skipped),
            SkipReasonCounts = skipReasonCounts,
            HostCounts = hostCounts
        };
    }

    public async Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default)
    {
        var result = await mContext.ScrapeAuditLog.DeleteManyAsync(
            a => a.JobId == jobId, ct);
        return result.DeletedCount;
    }

    public async Task<long> DeleteByLibraryVersionAsync(string libraryId, string version, CancellationToken ct = default)
    {
        var filter = Builders<ScrapeAuditLogEntry>.Filter.And(
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.LibraryId, libraryId),
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Version, version));
        var result = await mContext.ScrapeAuditLog.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }
}
```

Note: `SummarizeAsync` uses an in-memory aggregation for clarity. For very large jobs (>100K entries) replace with a Mongo `$group` aggregation pipeline. Out of scope for v1; revisit in Wave 2 if histogram pages feel slow.

- [ ] **Step 4: Run the tests to verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditRepositoryTests"`
Expected: PASS — all four tests green.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs SaddleRAG.Database/Repositories/ScrapeAuditRepository.cs SaddleRAG.Tests/Audit/ScrapeAuditRepositoryTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): add IScrapeAuditRepository with insert, query, summarize, delete
```

---

## Task 4: Buffered audit writer service

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IScrapeAuditWriter.cs`
- Create: `SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs`
- Create: `SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs`

- [ ] **Step 1: Write the failing writer tests**

```csharp
// SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Diagnostics;

namespace SaddleRAG.Tests.Audit;

public sealed class ScrapeAuditWriterTests
{
    [Fact]
    public async Task FlushesWhenBufferReachesSizeThreshold()
    {
        var spy = new SpyRepository();
        await using var writer = new ScrapeAuditWriter(spy, batchSize: 5,
                                                        flushInterval: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 5; i++)
            writer.RecordSkipped(NewCtx("job-1"), $"https://a.com/{i}",
                                  parentUrl: null, host: "a.com", depth: 1,
                                  AuditSkipReason.PatternExclude, detail: null);

        await writer.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, spy.Inserted.Count);
        Assert.All(spy.Inserted, e => Assert.Equal(AuditStatus.Skipped, e.Status));
    }

    [Fact]
    public async Task FlushesPeriodicallyByTime()
    {
        var spy = new SpyRepository();
        await using var writer = new ScrapeAuditWriter(spy, batchSize: 1000,
                                                        flushInterval: TimeSpan.FromMilliseconds(150));

        writer.RecordFetched(NewCtx("job-2"), "https://x.com/", null, "x.com", 0);

        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.Single(spy.Inserted);
        Assert.Equal(AuditStatus.Fetched, spy.Inserted[0].Status);
    }

    [Fact]
    public async Task DisposeFlushesRemainingEntries()
    {
        var spy = new SpyRepository();
        var writer = new ScrapeAuditWriter(spy, batchSize: 1000,
                                            flushInterval: TimeSpan.FromMinutes(5));
        writer.RecordIndexed(NewCtx("job-3"), "https://y.com/", null, "y.com", 0,
                              new AuditPageOutcome
                              {
                                  FetchStatus = "200 OK",
                                  Category = "Overview",
                                  ChunkCount = 3
                              });

        await writer.DisposeAsync();

        Assert.Single(spy.Inserted);
        Assert.Equal(AuditStatus.Indexed, spy.Inserted[0].Status);
        Assert.Equal(3, spy.Inserted[0].PageOutcome!.ChunkCount);
    }

    private static AuditContext NewCtx(string jobId) => new()
    {
        JobId = jobId,
        LibraryId = "lib",
        Version = "1.0"
    };

    private sealed class SpyRepository : IScrapeAuditRepository
    {
        public List<ScrapeAuditLogEntry> Inserted { get; } = new();

        public Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default)
        {
            Inserted.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId, AuditStatus? status,
                                                                    AuditSkipReason? skipReason, string? host,
                                                                    string? urlSubstring, int limit,
                                                                    CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScrapeAuditLogEntry>>(Array.Empty<ScrapeAuditLogEntry>());

        public Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default)
            => Task.FromResult<ScrapeAuditLogEntry?>(null);

        public Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<long> DeleteByLibraryVersionAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(0L);
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditWriterTests"`
Expected: FAIL — `ScrapeAuditWriter`, `IScrapeAuditWriter`, and `AuditContext` do not exist.

- [ ] **Step 3: Implement the writer**

```csharp
// SaddleRAG.Core/Interfaces/IScrapeAuditWriter.cs
using SaddleRAG.Core.Models.Audit;

namespace SaddleRAG.Core.Interfaces;

public interface IScrapeAuditWriter : IAsyncDisposable
{
    void RecordSkipped(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                        AuditSkipReason reason, string? detail);

    void RecordFetched(AuditContext ctx, string url, string? parentUrl, string host, int depth);

    void RecordFailed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                       string error);

    void RecordIndexed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                        AuditPageOutcome outcome);

    Task FlushAsync(CancellationToken ct = default);
}

public sealed class AuditContext
{
    public required string JobId { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
}
```

```csharp
// SaddleRAG.Ingestion/Diagnostics/ScrapeAuditWriter.cs
#region Usings

using System.Collections.Concurrent;
using System.Threading.Channels;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

public sealed class ScrapeAuditWriter : IScrapeAuditWriter
{
    public ScrapeAuditWriter(IScrapeAuditRepository repo,
                              int batchSize = DefaultBatchSize,
                              TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        mRepo = repo;
        mBatchSize = batchSize;
        mFlushInterval = flushInterval ?? DefaultFlushInterval;
        mChannel = Channel.CreateUnbounded<ScrapeAuditLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        mLoop = Task.Run(RunFlushLoopAsync);
    }

    private const int DefaultBatchSize = 500;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

    private readonly IScrapeAuditRepository mRepo;
    private readonly int mBatchSize;
    private readonly TimeSpan mFlushInterval;
    private readonly Channel<ScrapeAuditLogEntry> mChannel;
    private readonly Task mLoop;
    private readonly CancellationTokenSource mCts = new();

    public void RecordSkipped(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                                AuditSkipReason reason, string? detail)
        => Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Skipped,
                              reason, detail, null));

    public void RecordFetched(AuditContext ctx, string url, string? parentUrl, string host, int depth)
        => Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Fetched,
                              null, null, null));

    public void RecordFailed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                              string error)
        => Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Failed,
                              null, null, new AuditPageOutcome { Error = error }));

    public void RecordIndexed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                                AuditPageOutcome outcome)
        => Enqueue(BuildEntry(ctx, url, parentUrl, host, depth, AuditStatus.Indexed,
                              null, null, outcome));

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = new List<ScrapeAuditLogEntry>(mBatchSize);
        while (mChannel.Reader.TryRead(out var entry))
            batch.Add(entry);
        if (batch.Count > 0)
            await mRepo.InsertManyAsync(batch, ct);
    }

    public async ValueTask DisposeAsync()
    {
        mChannel.Writer.TryComplete();
        await mCts.CancelAsync();
        try { await mLoop; } catch (OperationCanceledException) { }
        await FlushAsync();
        mCts.Dispose();
    }

    private void Enqueue(ScrapeAuditLogEntry entry) => mChannel.Writer.TryWrite(entry);

    private async Task RunFlushLoopAsync()
    {
        var buffer = new List<ScrapeAuditLogEntry>(mBatchSize);
        var lastFlush = DateTime.UtcNow;
        while (!mCts.IsCancellationRequested)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(mCts.Token);
                timeoutCts.CancelAfter(mFlushInterval);
                while (await mChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                {
                    while (mChannel.Reader.TryRead(out var entry))
                    {
                        buffer.Add(entry);
                        if (buffer.Count >= mBatchSize)
                        {
                            await mRepo.InsertManyAsync(buffer, mCts.Token);
                            buffer.Clear();
                            lastFlush = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }

            if (buffer.Count > 0 && (DateTime.UtcNow - lastFlush) >= mFlushInterval)
            {
                await mRepo.InsertManyAsync(buffer, CancellationToken.None);
                buffer.Clear();
                lastFlush = DateTime.UtcNow;
            }
        }

        if (buffer.Count > 0)
            await mRepo.InsertManyAsync(buffer, CancellationToken.None);
    }

    private static ScrapeAuditLogEntry BuildEntry(AuditContext ctx, string url, string? parentUrl,
                                                    string host, int depth, AuditStatus status,
                                                    AuditSkipReason? reason, string? detail,
                                                    AuditPageOutcome? outcome) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        JobId = ctx.JobId,
        LibraryId = ctx.LibraryId,
        Version = ctx.Version,
        Url = url,
        ParentUrl = parentUrl,
        Host = host,
        Depth = depth,
        DiscoveredAt = DateTime.UtcNow,
        Status = status,
        SkipReason = reason,
        SkipDetail = detail,
        PageOutcome = outcome
    };
}
```

- [ ] **Step 4: Run the tests to verify pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ScrapeAuditWriterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Core/Interfaces/IScrapeAuditWriter.cs SaddleRAG.Ingestion/Diagnostics SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): add buffered ScrapeAuditWriter with batch and time-based flush
```

---

## Task 5: DI registration

**Files:**
- Modify: `SaddleRAG.Database/Repositories/RepositoryFactory.cs`
- Modify: `SaddleRAG.Mcp/Program.cs` (or wherever services register — search for `AddSingleton<IScrapeJobRepository`)

- [ ] **Step 1: Find the existing registration site**

```bash
grep -rn "AddSingleton<IScrapeJobRepository" SaddleRAG.Mcp/
```

Expected: A single hit, probably in `Program.cs` or a `ServiceCollectionExtensions` file.

- [ ] **Step 2: Write the failing DI smoke test**

Add to `SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs`:

```csharp
[Fact]
public void DependencyInjectionResolvesAuditWriterAndRepository()
{
    var services = new ServiceCollection();
    // Mirror whatever the existing test fixture does to register repositories
    // — typical pattern: services.AddSaddleRagDatabase(...)
    services.AddSingleton<IScrapeAuditRepository>(_ => new SpyRepository());
    services.AddSingleton<IScrapeAuditWriter>(sp =>
        new ScrapeAuditWriter(sp.GetRequiredService<IScrapeAuditRepository>()));

    using var sp = services.BuildServiceProvider();
    var writer = sp.GetRequiredService<IScrapeAuditWriter>();
    Assert.NotNull(writer);
}
```

(Add `using Microsoft.Extensions.DependencyInjection;` at the top.)

- [ ] **Step 3: Run the test to verify it currently passes (sanity)**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~DependencyInjectionResolvesAuditWriterAndRepository"`
Expected: PASS — this test verifies the wiring works in principle.

- [ ] **Step 4: Register in the real DI surface**

In the file you found in Step 1 (e.g. `SaddleRAG.Mcp/Program.cs`), add next to the `IScrapeJobRepository` registration:

```csharp
services.AddSingleton<IScrapeAuditRepository, ScrapeAuditRepository>();
services.AddSingleton<IScrapeAuditWriter>(sp =>
    new ScrapeAuditWriter(sp.GetRequiredService<IScrapeAuditRepository>()));
```

If `RepositoryFactory.cs` has accessor properties for repositories used by tools, add a corresponding property:

```csharp
public IScrapeAuditRepository ScrapeAudit => mProvider.GetRequiredService<IScrapeAuditRepository>();
```

- [ ] **Step 5: Build and commit**

Run: `dotnet build SaddleRAG.sln -p:TreatWarningsAsErrors=true`
Expected: Build succeeds with zero warnings.

```bash
git add SaddleRAG.Mcp SaddleRAG.Database/Repositories/RepositoryFactory.cs SaddleRAG.Tests/Audit/ScrapeAuditWriterTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): register ScrapeAuditWriter and repository in DI
```

---

## Task 6: Audit cleanup on rescrape

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ScrapeDocsTools.cs` (and/or wherever the existing scrape cleanup runs — grep for `ChunkRepository.DeleteByLibraryVersion` or similar)
- Create: `SaddleRAG.Tests/Audit/ScrapeAuditCleanupIntegrationTests.cs`

- [ ] **Step 1: Locate the existing cleanup path**

```bash
grep -rn "DeleteByLibraryVersion\|DeleteVersionAsync" SaddleRAG.Mcp/ SaddleRAG.Ingestion/
```

The cleanup is the code that runs at the start of `scrape_docs` (and `submit_url_correction` and `delete_version`) to clear prior chunks/pages for `(libraryId, version)` before the new scrape begins.

- [ ] **Step 2: Write the failing integration test**

```csharp
// SaddleRAG.Tests/Audit/ScrapeAuditCleanupIntegrationTests.cs
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Audit;

[Collection("Mongo")]
public sealed class ScrapeAuditCleanupIntegrationTests
{
    public ScrapeAuditCleanupIntegrationTests(MongoFixture fixture)
    {
        mContext = fixture.NewContext();
    }

    private readonly SaddleRagDbContext mContext;

    [Fact]
    public async Task NewScrapeClearsPriorAuditForSameLibraryVersion()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(mContext);

        var lib = $"lib-{Guid.NewGuid():N}";
        var version = "1.0";

        // Seed prior audit
        await repo.InsertManyAsync(new[]
        {
            new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = "old-job",
                LibraryId = lib,
                Version = version,
                Url = "https://old.com/",
                Host = "old.com",
                Depth = 0,
                DiscoveredAt = DateTime.UtcNow,
                Status = AuditStatus.Indexed
            }
        }, TestContext.Current.CancellationToken);

        // Trigger the cleanup path (call whatever the public cleanup entrypoint is — see Step 1)
        // Example placeholder; replace with the real method discovered in Step 1:
        await TriggerExistingCleanup(lib, version);

        var remaining = await repo.QueryAsync("old-job", null, null, null, null, 100,
                                              TestContext.Current.CancellationToken);
        Assert.Empty(remaining);
    }

    private async Task TriggerExistingCleanup(string lib, string version)
    {
        // Implementer: call the same method scrape_docs invokes to clear prior chunks/pages.
        // Update this body once Step 1 identifies the entrypoint.
        await Task.CompletedTask;
        throw new NotImplementedException("Replace with actual cleanup entrypoint discovered in Step 1.");
    }
}
```

- [ ] **Step 3: Run the test to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~NewScrapeClearsPriorAuditForSameLibraryVersion"`
Expected: FAIL — `NotImplementedException` (or whatever current behavior is).

- [ ] **Step 4: Wire audit cleanup into the existing path**

In the cleanup path located in Step 1, after the existing `DeleteByLibraryVersion*` calls for chunks/pages/etc., add:

```csharp
await scrapeAuditRepo.DeleteByLibraryVersionAsync(libraryId, version, ct);
```

The exact call site will depend on what Step 1 found. Inject `IScrapeAuditRepository` into the same constructor where the other repositories are injected.

Update the test's `TriggerExistingCleanup` to call the actual entrypoint.

- [ ] **Step 5: Run the test and commit**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~NewScrapeClearsPriorAuditForSameLibraryVersion"`
Expected: PASS.

```bash
git add -p   # cherry-pick only audit-related changes
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): clear prior audit log on rescrape of same (library, version)
```

---

## Task 7: Wire audit writer into PageCrawler filter sites

**Files:**
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs`
- Create: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`

The crawler has three filter sites (verified in the spec exploration):
- `IsAllowed(url, job)` called from `EnqueueDiscoveredLinks` (line ~1390 + helper at ~1733).
- Off-site / same-host depth check in `ProcessCrawlScopeAsync` (line ~925).
- `HostScopeFilter.IsGated(url)` in `HandleCrawlEntryAsync` (line ~883).

Each site gains a single call to the writer when a URL is rejected.

- [ ] **Step 1: Write the failing integration test against a fake site**

```csharp
// SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Diagnostics;

namespace SaddleRAG.Tests.Audit;

[Collection("Mongo")]
public sealed class CrawlerAuditIntegrationTests
{
    public CrawlerAuditIntegrationTests(MongoFixture fixture) { mFixture = fixture; }

    private readonly MongoFixture mFixture;

    [Fact]
    public async Task PatternExcludeIsAuditedBeforeFetch()
    {
        // Set up a tiny in-process Kestrel host serving:
        //   /         -> page with links to /a, /b, /excluded/c, /excluded/d
        //   /a        -> small doc page
        //   /b        -> small doc page
        //   /excluded -> any page
        // Crawler ScrapeJob with ExcludedUrlPatterns = ["/excluded/.*"].
        // Run the crawler; assert audit has Skipped/PatternExclude rows for /excluded/c and /excluded/d
        // and Fetched rows for /a and /b.

        // Use whatever in-test host pattern other integration tests use
        // (search SaddleRAG.Tests for `WebApplication.CreateBuilder` or similar).
        await Task.CompletedTask;
        Assert.Fail("Replace with concrete fixture wiring once located.");
    }
}
```

- [ ] **Step 2: Run the test to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"`
Expected: FAIL.

- [ ] **Step 3: Add audit writer to PageCrawler**

Inject `IScrapeAuditWriter` via the constructor (alongside whatever else PageCrawler accepts). Build an `AuditContext` from the active `ScrapeJob`'s `LibraryId`, `Version`, and `Id` once at the start of `CrawlAsync` and pass it down (or stash it on a per-job state object the existing code already uses).

Add audit calls at each filter site. Find the filter helper `IsAllowed`:

```csharp
// In IsAllowed (the helper), wrap the existing return false branches:
if (HasBinaryExtension(url))
{
    mAuditWriter.RecordSkipped(ctx, url, parentUrl, uri.Host, depth,
                                AuditSkipReason.BinaryExt, detail: null);
    return false;
}
if (job.AllowedUrlPatterns is { Count: > 0 } &&
    !job.AllowedUrlPatterns.Any(p => Regex.IsMatch(url, p)))
{
    mAuditWriter.RecordSkipped(ctx, url, parentUrl, uri.Host, depth,
                                AuditSkipReason.PatternMissAllowed, detail: null);
    return false;
}
if (job.ExcludedUrlPatterns is { Count: > 0 } &&
    job.ExcludedUrlPatterns.FirstOrDefault(p => Regex.IsMatch(url, p)) is { } matched)
{
    mAuditWriter.RecordSkipped(ctx, url, parentUrl, uri.Host, depth,
                                AuditSkipReason.PatternExclude, detail: matched);
    return false;
}
```

In `ProcessCrawlScopeAsync` — at the existing depth-skip code path, add:

```csharp
if (entry.IsOffSite && entry.Depth > job.OffSiteDepth)
{
    mAuditWriter.RecordSkipped(ctx, entry.Url, entry.ParentUrl, entry.Host, entry.Depth,
                                AuditSkipReason.OffSiteDepth,
                                detail: $"depth={entry.Depth} limit={job.OffSiteDepth}");
    continue;
}
if (entry.IsOnRootHost && entry.Depth > job.SameHostDepth)
{
    mAuditWriter.RecordSkipped(ctx, entry.Url, entry.ParentUrl, entry.Host, entry.Depth,
                                AuditSkipReason.SameHostDepth,
                                detail: $"depth={entry.Depth} limit={job.SameHostDepth}");
    continue;
}
```

In `HandleCrawlEntryAsync` at the `ctx.IsGated(...)` check:

```csharp
if (ctx.IsGated(entry.Url))
{
    mAuditWriter.RecordSkipped(auditCtx, entry.Url, entry.ParentUrl, entry.Host, entry.Depth,
                                AuditSkipReason.HostGated, detail: null);
    return;
}
```

The exact field names (`entry.IsOffSite`, `entry.ParentUrl`, etc.) may differ — read the existing code and adapt to the real types.

- [ ] **Step 4: Update the integration test, run it**

Wire the in-process Kestrel host (mirror an existing integration test that does this — search for `WebApplication.CreateBuilder` in `SaddleRAG.Tests`). Assert one row per expected outcome.

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Ingestion/Crawling/PageCrawler.cs SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): record skip reasons at every PageCrawler filter site
```

---

## Task 8: Wire audit writer into fetch outcomes

**Files:**
- Modify: `SaddleRAG.Ingestion/Crawling/PageCrawler.cs` (success and failure paths in `HandleCrawlEntryAsync` / `CompleteSuccessfulFetchAsync`)
- Modify: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`

- [ ] **Step 1: Add tests for fetched and failed outcomes**

Append to `CrawlerAuditIntegrationTests`:

```csharp
[Fact]
public async Task SuccessfulFetchProducesFetchedAuditRow() { /* assert Fetched row for /a */ }

[Fact]
public async Task TimeoutOr4xxProducesFailedAuditRow() { /* assert Failed row */ }
```

Use a route in the test host that returns 404 to force the failed path.

- [ ] **Step 2: Run the tests to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"`
Expected: FAIL on the new tests.

- [ ] **Step 3: Add the calls**

In the success path (where the page is fully fetched and queued onto `crawlToClassify`):

```csharp
mAuditWriter.RecordFetched(auditCtx, entry.Url, entry.ParentUrl, entry.Host, entry.Depth);
```

In the failure path (catch block / non-200 result):

```csharp
mAuditWriter.RecordFailed(auditCtx, entry.Url, entry.ParentUrl, entry.Host, entry.Depth,
                           error: ex.Message ?? response?.StatusText ?? "unknown");
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~CrawlerAuditIntegrationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Ingestion/Crawling/PageCrawler.cs SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): record fetch success and failure outcomes
```

---

## Task 9: Wire audit writer into per-page index completion

**Files:**
- Modify: `SaddleRAG.Ingestion/IngestionOrchestrator.cs`
- Modify: `SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs`

When a page reaches the end of the pipeline (committed to `Pages` and chunks indexed), upgrade its audit row from Fetched → Indexed and attach the `AuditPageOutcome`.

- [ ] **Step 1: Add a test for indexed outcome**

```csharp
[Fact]
public async Task IndexedPageHasOutcomeWithChunkCountAndCategory()
{
    // After full pipeline, /a should appear in audit as Status=Indexed with PageOutcome.ChunkCount > 0.
}
```

- [ ] **Step 2: Run the test to verify failure**

Expected: FAIL — currently only Fetched rows exist.

- [ ] **Step 3: Add upgrade-to-Indexed logic**

The audit log doesn't *upgrade* in place — for simplicity, write a *new* Indexed row alongside the Fetched row. The summary aggregations can dedupe by URL when needed. Or, alternative: keep only the Indexed row and drop the Fetched row at completion. Choose the simpler one: **write a separate Indexed row**.

In `IngestionOrchestrator.RunIndexStageAsync` (or the equivalent stage that signals "page fully done"), after the chunks for that page are written, add:

```csharp
mAuditWriter.RecordIndexed(auditCtx, page.Url, page.ParentUrl, page.Host, page.Depth,
                            new AuditPageOutcome
                            {
                                FetchStatus = page.FetchStatus,
                                Category = page.Category.ToString(),
                                ChunkCount = chunksWrittenForThisPage
                            });
```

Adjust field names to match the actual `Page` / `DocChunk` types in the codebase.

- [ ] **Step 4: Run the test**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Ingestion/IngestionOrchestrator.cs SaddleRAG.Tests/Audit/CrawlerAuditIntegrationTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): record indexed-page outcome with chunk count and category
```

---

## Task 10: `inspect_scrape` MCP tool — summary mode

**Files:**
- Create: `SaddleRAG.Mcp/Tools/InspectScrapeTool.cs`
- Create: `SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs`

- [ ] **Step 1: Write the failing summary test**

```csharp
// SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs
using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Audit;

[Collection("Mongo")]
public sealed class InspectScrapeToolTests
{
    public InspectScrapeToolTests(MongoFixture fixture) { mFixture = fixture; }

    private readonly MongoFixture mFixture;

    [Fact]
    public async Task SummaryReturnsCountsAndBuckets()
    {
        var ctx = mFixture.NewContext();
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);

        var jobId = $"job-{Guid.NewGuid():N}";
        await repo.InsertManyAsync(MakeMixedAudit(jobId, 100), TestContext.Current.CancellationToken);

        var factory = mFixture.NewRepositoryFactory();
        var json = await InspectScrapeTool.InspectScrape(factory, jobId,
            status: null, skipReason: null, host: null, url: null, limit: 10);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(jobId, root.GetProperty("JobId").GetString());
        Assert.True(root.GetProperty("Summary").GetProperty("TotalConsidered").GetInt32() > 0);
        Assert.True(root.GetProperty("Summary").GetProperty("SkipReasonCounts").EnumerateObject().Any());
    }

    private static IEnumerable<ScrapeAuditLogEntry> MakeMixedAudit(string jobId, int count)
    {
        for (var i = 0; i < count; i++)
            yield return new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                LibraryId = "lib",
                Version = "1.0",
                Url = $"https://example.com/{i}",
                Host = "example.com",
                Depth = 1,
                DiscoveredAt = DateTime.UtcNow,
                Status = (i % 4) switch
                {
                    0 => AuditStatus.Indexed,
                    1 => AuditStatus.Fetched,
                    2 => AuditStatus.Skipped,
                    _ => AuditStatus.Failed
                },
                SkipReason = (i % 4 == 2) ? AuditSkipReason.PatternExclude : null
            };
    }
}
```

- [ ] **Step 2: Run the test to verify failure**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~InspectScrapeToolTests"`
Expected: FAIL — `InspectScrapeTool` does not exist.

- [ ] **Step 3: Implement the tool — summary mode only**

```csharp
// SaddleRAG.Mcp/Tools/InspectScrapeTool.cs
#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

[McpServerToolType]
public static class InspectScrapeTool
{
    [McpServerTool(Name = "inspect_scrape")]
    [Description("Inspect a scrape's audit log. With no filter args, returns a top-level summary " +
                 "(kept/dropped totals, by-host breakdown, by-skip-reason histogram, sample URLs). " +
                 "With filters (status, skipReason, host, url), drills into matching entries.")]
    public static async Task<string> InspectScrape(RepositoryFactory factory,
                                                    [Description("Scrape job id")]
                                                    string jobId,
                                                    [Description("Optional status filter: Considered, Skipped, Fetched, Failed, Indexed")]
                                                    string? status = null,
                                                    [Description("Optional skip reason: PatternExclude, OffSiteDepth, BinaryExt, ...")]
                                                    string? skipReason = null,
                                                    [Description("Optional host filter")]
                                                    string? host = null,
                                                    [Description("Optional URL substring filter")]
                                                    string? url = null,
                                                    [Description("Max entries to return when filters applied (default 50)")]
                                                    int limit = 50)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var repo = factory.ScrapeAudit;

        var hasFilter = status != null || skipReason != null || host != null || url != null;
        if (!hasFilter)
        {
            var summary = await repo.SummarizeAsync(jobId);
            return JsonSerializer.Serialize(new
            {
                JobId = jobId,
                Mode = "summary",
                Summary = summary
            }, JsonOptions);
        }

        // Filter mode handled in Task 11
        return JsonSerializer.Serialize(new { JobId = jobId, Mode = "filter", Note = "TODO Task 11" },
                                        JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };
}
```

- [ ] **Step 4: Run the test**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~SummaryReturnsCountsAndBuckets"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Mcp/Tools/InspectScrapeTool.cs SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): inspect_scrape MCP tool — summary mode
```

---

## Task 11: `inspect_scrape` MCP tool — filtered + single-URL modes

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/InspectScrapeTool.cs`
- Modify: `SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs`

- [ ] **Step 1: Write the failing filter and URL tests**

```csharp
[Fact]
public async Task FilterByStatusReturnsMatchingEntries()
{
    // seed 100 mixed entries; call InspectScrape with status="Indexed" — assert all returned have Indexed.
}

[Fact]
public async Task FilterBySkipReasonReturnsMatchingEntries()
{
    // assert only PatternExclude rows are returned when skipReason="PatternExclude".
}

[Fact]
public async Task SingleUrlLookupReturnsLineage()
{
    // seed an entry with parent URL set; call with url=...; assert response includes ParentUrl.
}

[Fact]
public async Task UnknownJobIdReturnsNotFound()
{
    var factory = mFixture.NewRepositoryFactory();
    var json = await InspectScrapeTool.InspectScrape(factory, "no-such-job",
        null, null, null, null, 50);
    using var doc = JsonDocument.Parse(json);
    Assert.Equal("not_found", doc.RootElement.GetProperty("Status").GetString());
}
```

- [ ] **Step 2: Run the tests to verify failure**

Expected: FAIL — filter mode not implemented.

- [ ] **Step 3: Implement filter and URL modes**

Replace the `// Filter mode handled in Task 11` block in `InspectScrapeTool` with:

```csharp
// Single-URL forensic
if (!string.IsNullOrEmpty(url))
{
    var entry = await repo.GetByUrlAsync(jobId, url);
    if (entry is null)
    {
        return JsonSerializer.Serialize(new
        {
            JobId = jobId,
            Mode = "url",
            Status = "not_found",
            Url = url
        }, JsonOptions);
    }
    return JsonSerializer.Serialize(new
    {
        JobId = jobId,
        Mode = "url",
        Status = "found",
        Entry = entry
    }, JsonOptions);
}

// Bucketed/filtered query
AuditStatus? statusEnum = ParseEnum<AuditStatus>(status);
AuditSkipReason? reasonEnum = ParseEnum<AuditSkipReason>(skipReason);
var entries = await repo.QueryAsync(jobId, statusEnum, reasonEnum, host,
                                      urlSubstring: null, limit);
if (entries.Count == 0)
{
    var jobExists = (await repo.SummarizeAsync(jobId)).TotalConsidered > 0;
    if (!jobExists)
    {
        return JsonSerializer.Serialize(new
        {
            JobId = jobId,
            Mode = "filter",
            Status = "not_found"
        }, JsonOptions);
    }
}
return JsonSerializer.Serialize(new
{
    JobId = jobId,
    Mode = "filter",
    AppliedFilters = new { Status = status, SkipReason = skipReason, Host = host, Url = url, Limit = limit },
    Count = entries.Count,
    Entries = entries
}, JsonOptions);
```

Add the helper:

```csharp
private static T? ParseEnum<T>(string? raw) where T : struct, Enum
    => string.IsNullOrEmpty(raw) ? null : Enum.TryParse<T>(raw, ignoreCase: true, out var v) ? v : null;
```

- [ ] **Step 4: Run the tests**

Expected: All four new tests PASS.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Mcp/Tools/InspectScrapeTool.cs SaddleRAG.Tests/Audit/InspectScrapeToolTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
feat(audit): inspect_scrape filter and single-URL modes
```

---

## Task 12: `dryrun_scrape` async refactor

**Files:**
- Modify: the file currently hosting `dryrun_scrape` (grep for `Name = "dryrun_scrape"`)
- Modify: existing dryrun pipeline to write audit entries during the crawl
- Modify: `SaddleRAG.Tests/...` (whichever existing test covers dryrun_scrape)
- Create: `SaddleRAG.Tests/Audit/DryRunAuditIntegrationTests.cs`

- [ ] **Step 1: Locate dryrun_scrape**

```bash
grep -rn 'Name = "dryrun_scrape"' SaddleRAG.Mcp/
```

Currently it runs synchronously and returns a `DryRunReport`. The refactor changes it to enqueue a background job and return `{ JobId, Status: "Queued" }` immediately, matching `scrape_docs`. The dryrun crawl still runs the full Playwright fetch but skips classify/chunk/embed/index. The audit writer is still attached so audit rows are written.

The Wave 1 prerequisite is the existing `BackgroundJobQueue` infrastructure (per `docs/superpowers/specs/2026-04-30-background-job-queue-design.md`) which already supports queuing arbitrary `JobType` values.

- [ ] **Step 2: Write the failing test**

```csharp
// SaddleRAG.Tests/Audit/DryRunAuditIntegrationTests.cs
[Collection("Mongo")]
public sealed class DryRunAuditIntegrationTests
{
    [Fact]
    public async Task DryRunReturnsJobIdImmediatelyAndPopulatesAudit()
    {
        // Call dryrun_scrape against a tiny in-process Kestrel host.
        // Assert: returns { JobId, Status: "Queued" } in under 200ms.
        // Wait for job completion via get_job_status.
        // Assert: ScrapeAuditLog has rows for the visited URLs (Fetched but not Indexed).
        // Assert: Pages and Chunks collections have NO rows for this run.
    }
}
```

- [ ] **Step 3: Run the test to verify failure**

Expected: FAIL — dryrun_scrape currently waits for completion.

- [ ] **Step 4: Refactor dryrun_scrape**

In the `dryrun_scrape` handler:

```csharp
public static async Task<string> DryrunScrape(RepositoryFactory factory,
                                                IBackgroundJobQueue jobQueue,
                                                /* existing args */ )
{
    var job = new DryRunJobInput { /* args */ };
    var jobId = await jobQueue.EnqueueAsync(JobTypes.DryRun, JsonSerializer.Serialize(job));
    return JsonSerializer.Serialize(new
    {
        JobId = jobId,
        Status = "Queued",
        Message = $"Dryrun queued. Poll inspect_scrape with jobId='{jobId}' once complete."
    }, JsonOptions);
}
```

Add a `DryRunJobHandler` that the queue dispatches:

```csharp
public sealed class DryRunJobHandler : IBackgroundJobHandler
{
    public string JobType => JobTypes.DryRun;
    public async Task ExecuteAsync(BackgroundJobRecord record, CancellationToken ct)
    {
        // Run PageCrawler against the input URL, attach IScrapeAuditWriter, do NOT push fetched
        // pages onto the classify channel. The audit writer captures rows; chunks/pages are not
        // written.
    }
}
```

The exact wiring depends on the existing background-job infrastructure introduced in the prior spec. Mirror how `RechunkLibrary` or `IndexProjectDependencies` register their handlers.

- [ ] **Step 5: Run the test and commit**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~DryRunAuditIntegrationTests"`
Expected: PASS.

```bash
git add SaddleRAG.Mcp SaddleRAG.Ingestion SaddleRAG.Tests/Audit/DryRunAuditIntegrationTests.cs
git commit -F msg.txt
```
where `msg.txt`:
```
refactor(dryrun): convert dryrun_scrape to async/queued, write audit during crawl
```

---

## Final verification

- [ ] **Build with warnings-as-errors**

```bash
dotnet build SaddleRAG.sln -p:TreatWarningsAsErrors=true
```

Expected: zero warnings, zero errors.

- [ ] **Run full test suite**

```bash
dotnet test SaddleRAG.sln
```

Expected: all green, including all new audit tests.

- [ ] **Manual sanity check via Claude Code**

```
- Trigger a small scrape via scrape_docs against any short docs site
- Wait for it to complete
- Run inspect_scrape with the resulting jobId, no filters
- Confirm Summary shows expected indexed/skipped counts
- Run inspect_scrape with status="Skipped" — confirm a list of dropped URLs with reasons
- Run inspect_scrape with url="https://...." for a specific URL — confirm lineage details
```

---

## Wave 1 Complete

After all tasks pass:
- The `ScrapeAuditLog` collection is populated for every scrape and dryrun.
- The `inspect_scrape` MCP tool gives the LLM full forensic access.
- `dryrun_scrape` is async and uses the same audit surface as a real scrape.
- Wave 2 (Blazor monitor + SignalR + UI) is unblocked: `MonitorBroadcaster` will hook into the same call sites already used here.

## Open questions during implementation

- **`AuditContext` propagation** — the cleanest place to construct it is at `CrawlAsync` entry. If the crawler doesn't already plumb a per-job context object through, prefer adding one rather than passing five separate args to `RecordSkipped`/`RecordFetched`/etc.
- **Indexed row vs. upgrade** — the plan writes a separate Indexed row alongside the original Fetched row. Verify with the user during code review whether dedupe logic in `inspect_scrape` summary is needed (currently summary counts Fetched and Indexed separately, which double-counts). If so, add `repo.UpsertAsync` and switch the indexed call to upsert by `(jobId, url)`. Flag during review.
- **Test fixture for in-process Kestrel** — confirm whether `SaddleRAG.Tests` already has a pattern; if not, add one in Task 7 and reuse in Task 12.
