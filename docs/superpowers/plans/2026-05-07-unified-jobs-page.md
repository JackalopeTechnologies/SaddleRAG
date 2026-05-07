# Unified Jobs Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface every job type — scrape, dry-run, rechunk, rescrub, rename, delete, deps-index, url-correction — on `/monitor/jobs`, and bring all of them to live-update parity via SignalR.

**Architecture:** Read-side merge across `IScrapeJobRepository`, `IBackgroundJobRepository`, `IRescrubJobRepository` behind a new `IUnifiedJobView` service. Plumb `IMonitorBroadcaster` calls into `BackgroundJobRunner`, `RescrubJobRunner`, and the dry-run path so each runner emits the same `JobStarted`/`JobProgress`/`JobCompleted`/`JobFailed`/`JobCancelled` SignalR events that scrapes already do. No DB migration.

**Tech Stack:** .NET 8, Blazor Server, MudBlazor, SignalR, MongoDB, xUnit, NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-05-07-unified-jobs-page-design.md`](../specs/2026-05-07-unified-jobs-page-design.md)

---

## File Structure

**Create:**
- `SaddleRAG.Core/Models/Monitor/JobType.cs` — discriminator enum.
- `SaddleRAG.Core/Models/Monitor/JobRow.cs` — common row record returned by the unified view.
- `SaddleRAG.Core/Models/Monitor/JobProgressEvent.cs` — SignalR payload for live progress ticks.
- `SaddleRAG.Core/Interfaces/IUnifiedJobView.cs` — read-side interface.
- `SaddleRAG.Monitor/Services/UnifiedJobView.cs` — implementation (parallel reads + project + sort + filter + limit).
- `SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs` — unit tests for projection, merge, sort, filter.
- `SaddleRAG.Tests/Monitor/FakeBackgroundJobRepository.cs` — test fake.
- `SaddleRAG.Tests/Monitor/FakeRescrubJobRepository.cs` — test fake.
- `SaddleRAG.Tests/Ingestion/BackgroundJobRunnerBroadcasterTests.cs` — verifies started/progress/completed/failed/cancelled.

**Modify:**
- `SaddleRAG.Core/Interfaces/IMonitorEvents.cs` — add `JobProgress` event.
- `SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs` — add `RecordJobProgress`.
- `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs` — implement `RecordJobProgress`; relax `RecordJobStarted` guards to allow empty library/version/rootUrl.
- `SaddleRAG.Mcp/Hubs/MonitorLifecycleRelay.cs` — relay `JobProgress` over SignalR.
- `SaddleRAG.Monitor/Services/MonitorJobService.cs` — delegate `ListAsync` to `IUnifiedJobView`.
- `SaddleRAG.Monitor/Services/MonitorDataService.cs` — `GetJobInfoAsync` and `GetLatestJobIdAsync` use `IUnifiedJobView`.
- `SaddleRAG.Monitor/Pages/JobHistoryPage.razor` — new column layout, type chip, target, progress.
- `SaddleRAG.Monitor/Pages/JobHistoryPage.razor.cs` — type filter binding.
- `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs` — gracefully render non-scrape jobs.
- `SaddleRAG.Mcp/Tools/IngestionTools.cs` — wrap `crawler.DryRunAsync` invocation with broadcaster lifecycle calls.
- `SaddleRAG.Ingestion/BackgroundJobRunner.cs` — broadcaster lifecycle around `RunJobAsync`; wrap `onProgress` to also call `RecordJobProgress`.
- `SaddleRAG.Ingestion/RescrubJobRunner.cs` — broadcaster lifecycle and progress emit.
- `SaddleRAG.Mcp/Program.cs` — register `IUnifiedJobView`.
- `SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs` — extend to verify pass-through to fake unified view.
- `SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs` — assert `JobProgress` event fires.

---

## Task 1: Add `JobType` enum and `JobRow` record

**Files:**
- Create: `SaddleRAG.Core/Models/Monitor/JobType.cs`
- Create: `SaddleRAG.Core/Models/Monitor/JobRow.cs`

- [ ] **Step 1: Create `JobType` enum**

```csharp
// JobType.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Discriminator for jobs surfaced by <see cref="SaddleRAG.Core.Interfaces.IUnifiedJobView" />.
/// </summary>
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
    SubmitUrlCorrection
}
```

- [ ] **Step 2: Create `JobRow` record**

```csharp
// JobRow.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Common row shape returned by <see cref="SaddleRAG.Core.Interfaces.IUnifiedJobView" />.
///     Projects every job-storage type into a single display model.
/// </summary>
public sealed record JobRow
{
    public required string JobId { get; init; }
    public required JobType Type { get; init; }
    public required ScrapeJobStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    public string? LibraryId { get; init; }
    public string? Version { get; init; }
    public string? RenameToId { get; init; }
    public string? ScanPath { get; init; }

    public int ItemsProcessed { get; init; }
    public int ItemsTotal { get; init; }
    public string? ItemsLabel { get; init; }

    public int ErrorCount { get; init; }
    public string? ErrorMessage { get; init; }

    public TimeSpan? Duration =>
        StartedAt is null ? null : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
}
```

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build SaddleRAG.Core/SaddleRAG.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add SaddleRAG.Core/Models/Monitor/JobType.cs SaddleRAG.Core/Models/Monitor/JobRow.cs
git commit -F - <<'EOF'
feat(monitor): add JobType and JobRow common types

Foundation for the unified jobs page. JobType discriminates scrape,
dry-run, rechunk, rescrub, rename, delete-version, delete-library,
index-deps, and url-correction. JobRow is the common shape produced by
the upcoming IUnifiedJobView.
EOF
```

---

## Task 2: Add `JobProgressEvent` and extend `IMonitorEvents`

**Files:**
- Create: `SaddleRAG.Core/Models/Monitor/JobProgressEvent.cs`
- Modify: `SaddleRAG.Core/Interfaces/IMonitorEvents.cs`

- [ ] **Step 1: Create `JobProgressEvent`**

```csharp
// JobProgressEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Live progress tick for a job that reports incremental counts
///     (rechunk → chunks, dry-run → pages, deps-index → packages).
/// </summary>
public sealed record JobProgressEvent
{
    public required string JobId { get; init; }
    public required int ItemsProcessed { get; init; }
    public required int ItemsTotal { get; init; }
    public required string ItemsLabel { get; init; }
}
```

- [ ] **Step 2: Add the event to `IMonitorEvents`**

Modify `SaddleRAG.Core/Interfaces/IMonitorEvents.cs`. Add the new event between the existing four lifecycle events and `SuspectFlagRaised`:

```csharp
public interface IMonitorEvents
{
    event Action<JobStartedEvent>?   JobStarted;
    event Action<JobProgressEvent>?  JobProgress;
    event Action<JobCompletedEvent>? JobCompleted;
    event Action<JobFailedEvent>?    JobFailed;
    event Action<JobCancelledEvent>? JobCancelled;
    event Action<SuspectFlagEvent>?  SuspectFlagRaised;
}
```

- [ ] **Step 3: Build (will fail because broadcaster doesn't implement the new event yet)**

Run: `dotnet build`
Expected: Compile error — `MonitorBroadcaster` does not implement `JobProgress`. We fix this in Task 3.

- [ ] **Step 4: Commit (build broken intentionally — restored in Task 3)**

```bash
git add SaddleRAG.Core/Models/Monitor/JobProgressEvent.cs SaddleRAG.Core/Interfaces/IMonitorEvents.cs
git commit -F - <<'EOF'
feat(monitor): add JobProgress event to IMonitorEvents

Broadcaster impl follows in the next commit; this commit on its own
intentionally breaks the build briefly. Subagent runners that build
after each task should expect the failure here and resolution in the
next task.
EOF
```

---

## Task 3: Implement `RecordJobProgress` on `MonitorBroadcaster`

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs`
- Modify: `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`
- Modify: `SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs`

- [ ] **Step 1: Add a failing test for `JobProgress`**

Open `SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs` and append (before the closing brace):

```csharp
[Fact]
public void RecordJobProgressFiresJobProgressEvent()
{
    var broadcaster = new MonitorBroadcaster();
    JobProgressEvent? captured = null;
    broadcaster.JobProgress += e => captured = e;

    broadcaster.RecordJobProgress("job-7", processed: 17, total: 42, label: "chunks");

    Assert.NotNull(captured);
    Assert.Equal("job-7", captured!.JobId);
    Assert.Equal(17, captured.ItemsProcessed);
    Assert.Equal(42, captured.ItemsTotal);
    Assert.Equal("chunks", captured.ItemsLabel);
}
```

- [ ] **Step 2: Run the test to verify it fails (compile error)**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RecordJobProgressFires"`
Expected: Compile error — `RecordJobProgress` not defined on `MonitorBroadcaster`.

- [ ] **Step 3: Add `RecordJobProgress` to `IMonitorBroadcaster`**

Append to `SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs` after `RecordJobCancelled`:

```csharp
void RecordJobProgress(string jobId, int processed, int total, string label);
```

- [ ] **Step 4: Implement `RecordJobProgress` in `MonitorBroadcaster`**

In `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`, add the event field next to the existing ones:

```csharp
public event Action<JobProgressEvent>? JobProgress;
```

Add the method (place it after `RecordJobCancelled`):

```csharp
public void RecordJobProgress(string jobId, int processed, int total, string label)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);
    ArgumentException.ThrowIfNullOrEmpty(label);
    SafeRaise(() => JobProgress?.Invoke(new JobProgressEvent
                                            {
                                                JobId          = jobId,
                                                ItemsProcessed = processed,
                                                ItemsTotal     = total,
                                                ItemsLabel     = label
                                            }
                                       )
             );
}
```

- [ ] **Step 5: Run the new test — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RecordJobProgressFires"`
Expected: PASS.

- [ ] **Step 6: Run the full broadcaster test class to confirm no regressions**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorBroadcasterEventsTests"`
Expected: All pass.

- [ ] **Step 7: Commit**

```bash
git add SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs
git commit -F - <<'EOF'
feat(monitor): RecordJobProgress fires JobProgress event

Adds the broadcaster method and event channel that counted background
jobs (rechunk, dry-run, deps-index, rescrub) will use to emit live
progress ticks to SignalR.
EOF
```

---

## Task 4: Relax `MonitorBroadcaster.RecordJobStarted` guards

**Files:**
- Modify: `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`
- Modify: `SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs`

Background: `RecordJobStarted` today rejects empty `libraryId`, `version`, and `rootUrl`. Binary background jobs (rename, deps-index) legitimately have empty values for some of those. The guards become null-only.

- [ ] **Step 1: Add a failing test that passes empty strings**

Append to `MonitorBroadcasterEventsTests.cs`:

```csharp
[Fact]
public void RecordJobStartedAcceptsEmptyLibraryVersionRootUrl()
{
    var broadcaster = new MonitorBroadcaster();
    JobStartedEvent? captured = null;
    broadcaster.JobStarted += e => captured = e;

    broadcaster.RecordJobStarted("job-9", libraryId: string.Empty, version: string.Empty, rootUrl: string.Empty);

    Assert.NotNull(captured);
    Assert.Equal("job-9", captured!.JobId);
    Assert.Equal(string.Empty, captured.LibraryId);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RecordJobStartedAcceptsEmpty"`
Expected: FAIL — `ArgumentException: The value cannot be an empty string.`

- [ ] **Step 3: Relax the guards in `RecordJobStarted`**

Replace the four `ArgumentException.ThrowIfNullOrEmpty` calls at the top of `RecordJobStarted` with:

```csharp
public void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);
    ArgumentNullException.ThrowIfNull(libraryId);
    ArgumentNullException.ThrowIfNull(version);
    ArgumentNullException.ThrowIfNull(rootUrl);
    // ... rest unchanged
}
```

- [ ] **Step 4: Run the test — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~RecordJobStartedAcceptsEmpty"`
Expected: PASS.

- [ ] **Step 5: Run the full broadcaster test class**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorBroadcasterEventsTests"`
Expected: All pass.

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs SaddleRAG.Tests/Monitor/MonitorBroadcasterEventsTests.cs
git commit -F - <<'EOF'
fix(monitor): allow empty library/version/rootUrl in RecordJobStarted

Binary background jobs (rename, deps-index) legitimately have no
library or version. Guards now reject only null. Job id remains
required and non-empty.
EOF
```

---

## Task 5: Forward `JobProgress` over SignalR

**Files:**
- Modify: `SaddleRAG.Mcp/Hubs/MonitorLifecycleRelay.cs`

- [ ] **Step 1: Subscribe to `JobProgress` and forward**

Replace the relevant section of `MonitorLifecycleRelay.cs`:

```csharp
public Task StartAsync(CancellationToken ct)
{
    mEvents.JobStarted        += OnJobStarted;
    mEvents.JobProgress       += OnJobProgress;
    mEvents.JobCompleted      += OnJobCompleted;
    mEvents.JobFailed         += OnJobFailed;
    mEvents.JobCancelled      += OnJobCancelled;
    mEvents.SuspectFlagRaised += OnSuspectFlag;
    return Task.CompletedTask;
}

public Task StopAsync(CancellationToken ct)
{
    mEvents.JobStarted        -= OnJobStarted;
    mEvents.JobProgress       -= OnJobProgress;
    mEvents.JobCompleted      -= OnJobCompleted;
    mEvents.JobFailed         -= OnJobFailed;
    mEvents.JobCancelled      -= OnJobCancelled;
    mEvents.SuspectFlagRaised -= OnSuspectFlag;
    return Task.CompletedTask;
}

private void OnJobStarted(JobStartedEvent e) => Send(JobStartedMethod, e);
private void OnJobProgress(JobProgressEvent e) => Send(JobProgressMethod, e);
private void OnJobCompleted(JobCompletedEvent e) => Send(JobCompletedMethod, e);
private void OnJobFailed(JobFailedEvent e) => Send(JobFailedMethod, e);
private void OnJobCancelled(JobCancelledEvent e) => Send(JobCancelledMethod, e);
private void OnSuspectFlag(SuspectFlagEvent e) => Send(SuspectFlagMethod, e);
```

Add the constant alongside the others:

```csharp
private const string JobProgressMethod = "JobProgress";
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add SaddleRAG.Mcp/Hubs/MonitorLifecycleRelay.cs
git commit -F - <<'EOF'
feat(monitor): relay JobProgress event over SignalR

Browser clients now receive JobProgress messages alongside the
existing JobStarted/Completed/Failed/Cancelled lifecycle stream.
EOF
```

---

## Task 6: Define `IUnifiedJobView`

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IUnifiedJobView.cs`

- [ ] **Step 1: Create the interface**

```csharp
// IUnifiedJobView.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Read-side view that unions ScrapeJobs, BackgroundJobs, and RescrubJobs
///     into a single ordered <see cref="JobRow" /> list for the monitor UI.
/// </summary>
public interface IUnifiedJobView
{
    /// <summary>
    ///     Lists recent jobs across all storage paths, projected and filtered
    ///     to a common shape. Sorted by <see cref="JobRow.CreatedAt" /> desc.
    /// </summary>
    Task<IReadOnlyList<JobRow>> ListAsync(ScrapeJobStatus? statusFilter,
                                          JobType? typeFilter,
                                          string? libraryFilter,
                                          int limit,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Returns a single job by id from any storage path, or null if no
    ///     job with that id exists.
    /// </summary>
    Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add SaddleRAG.Core/Interfaces/IUnifiedJobView.cs
git commit -F - <<'EOF'
feat(monitor): introduce IUnifiedJobView interface

Read-side seam that the monitor will use instead of talking to three
job repositories directly. Implementation lands in the next commit.
EOF
```

---

## Task 7: Test fakes for background and rescrub repositories

**Files:**
- Create: `SaddleRAG.Tests/Monitor/FakeBackgroundJobRepository.cs`
- Create: `SaddleRAG.Tests/Monitor/FakeRescrubJobRepository.cs`

These mirror `FakeScrapeJobRepository` and let `UnifiedJobViewTests` populate test data without touching MongoDB.

- [ ] **Step 1: Create `FakeBackgroundJobRepository`**

```csharp
// FakeBackgroundJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeBackgroundJobRepository : IBackgroundJobRepository
{
    private readonly List<BackgroundJobRecord> mJobs = new();

    public void Add(BackgroundJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        mJobs.Add(job);
    }

    public Task UpsertAsync(BackgroundJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = mJobs.FindIndex(j => j.Id == job.Id);
        if (existing >= 0)
            mJobs[existing] = job;
        else
            mJobs.Add(job);
        return Task.CompletedTask;
    }

    public Task<BackgroundJobRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(mJobs.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<BackgroundJobRecord>> ListRecentAsync(string? jobType = null,
                                                                    int limit = 20,
                                                                    CancellationToken ct = default)
    {
        IReadOnlyList<BackgroundJobRecord> result = mJobs
                                                   .Where(j => jobType is null || j.JobType == jobType)
                                                   .OrderByDescending(j => j.CreatedAt)
                                                   .Take(limit)
                                                   .ToList();
        return Task.FromResult(result);
    }
}
```

- [ ] **Step 2: Create `FakeRescrubJobRepository`**

```csharp
// FakeRescrubJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeRescrubJobRepository : IRescrubJobRepository
{
    private readonly List<RescrubJobRecord> mJobs = new();

    public void Add(RescrubJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        mJobs.Add(job);
    }

    public Task UpsertAsync(RescrubJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = mJobs.FindIndex(j => j.Id == job.Id);
        if (existing >= 0)
            mJobs[existing] = job;
        else
            mJobs.Add(job);
        return Task.CompletedTask;
    }

    public Task<RescrubJobRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(mJobs.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<RescrubJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        IReadOnlyList<RescrubJobRecord> result = mJobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToList();
        return Task.FromResult(result);
    }
}
```

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build SaddleRAG.Tests/SaddleRAG.Tests.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add SaddleRAG.Tests/Monitor/FakeBackgroundJobRepository.cs SaddleRAG.Tests/Monitor/FakeRescrubJobRepository.cs
git commit -F - <<'EOF'
test: fakes for IBackgroundJobRepository and IRescrubJobRepository
EOF
```

---

## Task 8: TDD `UnifiedJobView` — projection from each source

**Files:**
- Create: `SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs`
- Create: `SaddleRAG.Monitor/Services/UnifiedJobView.cs`

This is the load-bearing task — projection logic for every source row type. Add tests in increments.

- [ ] **Step 1: Write the first failing test (scrape projection)**

Create `SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs`:

```csharp
// UnifiedJobViewTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class UnifiedJobViewTests
{
    [Fact]
    public async Task ScrapeJobProjectsToScrapeRow()
    {
        var scrape = new FakeScrapeJobRepository();
        scrape.Add(new ScrapeJobRecord
                       {
                           Id = "s1",
                           Job = new ScrapeJob { LibraryId = "foo", Version = "1.0", RootUrl = "https://x" },
                           Status = ScrapeJobStatus.Completed,
                           CreatedAt = DateTime.UtcNow,
                           PagesCompleted = 42,
                           ErrorCount = 0
                       }
                  );

        var view = new UnifiedJobView(scrape, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null, typeFilter: null, libraryFilter: null, limit: 10);

        var row = Assert.Single(rows);
        Assert.Equal("s1", row.JobId);
        Assert.Equal(JobType.Scrape, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal("1.0", row.Version);
        Assert.Equal(42, row.ItemsProcessed);
        Assert.Equal("pages", row.ItemsLabel);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests"`
Expected: Compile error — `UnifiedJobView` not defined.

- [ ] **Step 3: Create the implementation skeleton with scrape projection only**

Create `SaddleRAG.Monitor/Services/UnifiedJobView.cs`:

```csharp
// UnifiedJobView.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Reads three job collections in parallel, projects each into a
///     <see cref="JobRow" />, applies filters, sorts by CreatedAt desc,
///     and truncates to <c>limit</c>.
/// </summary>
public sealed class UnifiedJobView : IUnifiedJobView
{
    public UnifiedJobView(IScrapeJobRepository scrapeJobs,
                          IBackgroundJobRepository backgroundJobs,
                          IRescrubJobRepository rescrubJobs)
    {
        ArgumentNullException.ThrowIfNull(scrapeJobs);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(rescrubJobs);
        mScrape = scrapeJobs;
        mBackground = backgroundJobs;
        mRescrub = rescrubJobs;
    }

    private readonly IScrapeJobRepository mScrape;
    private readonly IBackgroundJobRepository mBackground;
    private readonly IRescrubJobRepository mRescrub;

    public async Task<IReadOnlyList<JobRow>> ListAsync(ScrapeJobStatus? statusFilter,
                                                       JobType? typeFilter,
                                                       string? libraryFilter,
                                                       int limit,
                                                       CancellationToken ct = default)
    {
        var fetchLimit = Math.Max(limit * 2, limit);
        var scrapeTask = mScrape.ListRecentAsync(fetchLimit, ct);
        var backgroundTask = mBackground.ListRecentAsync(jobType: null, fetchLimit, ct);
        var rescrubTask = mRescrub.ListRecentAsync(fetchLimit, ct);
        await Task.WhenAll(scrapeTask, backgroundTask, rescrubTask);

        IEnumerable<JobRow> rows = scrapeTask.Result.Select(ProjectScrape);
        // Background and rescrub projections land in subsequent steps.

        var filtered = rows
                      .Where(r => statusFilter is null || r.Status == statusFilter)
                      .Where(r => typeFilter is null || r.Type == typeFilter)
                      .Where(r => string.IsNullOrEmpty(libraryFilter)
                               || (r.LibraryId is not null
                                && r.LibraryId.Contains(libraryFilter, StringComparison.OrdinalIgnoreCase)))
                      .OrderByDescending(r => r.CreatedAt)
                      .ThenBy(r => r.JobId, StringComparer.Ordinal)
                      .Take(limit)
                      .ToList();
        return filtered;
    }

    public async Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobRow? result = null;
        var scrape = await mScrape.GetAsync(jobId, ct);
        if (scrape is not null)
            result = ProjectScrape(scrape);
        // Background and rescrub lookups land in subsequent steps.
        return result;
    }

    private static JobRow ProjectScrape(ScrapeJobRecord r) => new JobRow
                                                                  {
                                                                      JobId          = r.Id,
                                                                      Type           = JobType.Scrape,
                                                                      Status         = r.Status,
                                                                      CreatedAt      = r.CreatedAt,
                                                                      StartedAt      = r.StartedAt,
                                                                      CompletedAt    = r.CompletedAt,
                                                                      LibraryId      = r.Job.LibraryId,
                                                                      Version        = r.Job.Version,
                                                                      ItemsProcessed = r.PagesCompleted,
                                                                      ItemsTotal     = 0,
                                                                      ItemsLabel     = "pages",
                                                                      ErrorCount     = r.ErrorCount,
                                                                      ErrorMessage   = r.ErrorMessage
                                                                  };
}
```

- [ ] **Step 4: Run the scrape test — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests.ScrapeJobProjects"`
Expected: PASS.

- [ ] **Step 5: Add a failing test for background job projection (dryrun)**

Append to `UnifiedJobViewTests`:

```csharp
[Fact]
public async Task DryRunBackgroundJobProjectsToDryRunRow()
{
    var bg = new FakeBackgroundJobRepository();
    bg.Add(new BackgroundJobRecord
               {
                   Id = "b1",
                   JobType = BackgroundJobTypes.DryRunScrape,
                   LibraryId = "foo",
                   Version = "1.0",
                   InputJson = "{}",
                   Status = ScrapeJobStatus.Completed,
                   CreatedAt = DateTime.UtcNow,
                   ItemsProcessed = 50,
                   ItemsTotal = 50,
                   ItemsLabel = "pages"
               }
          );

    var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
    var rows = await view.ListAsync(null, null, null, 10);

    var row = Assert.Single(rows);
    Assert.Equal(JobType.DryRunScrape, row.Type);
    Assert.Equal("foo", row.LibraryId);
    Assert.Equal(50, row.ItemsProcessed);
    Assert.Equal("pages", row.ItemsLabel);
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests.DryRunBackgroundJobProjects"`
Expected: FAIL — list is empty (no projection yet for background).

- [ ] **Step 7: Add background projection in `UnifiedJobView`**

In `UnifiedJobView.ListAsync`, change the rows assignment to include background:

```csharp
IEnumerable<JobRow> rows = scrapeTask.Result.Select(ProjectScrape)
                                            .Concat(backgroundTask.Result.Select(ProjectBackground));
```

Also update `GetAsync`:

```csharp
public async Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);
    JobRow? result = null;
    var scrape = await mScrape.GetAsync(jobId, ct);
    if (scrape is not null)
        result = ProjectScrape(scrape);

    if (result is null)
    {
        var bg = await mBackground.GetAsync(jobId, ct);
        if (bg is not null)
            result = ProjectBackground(bg);
    }
    return result;
}
```

Add the private projection method (place after `ProjectScrape`):

```csharp
private static JobRow ProjectBackground(BackgroundJobRecord r)
{
    var (renameTo, scanPath) = ParseInputJson(r);
    return new JobRow
               {
                   JobId          = r.Id,
                   Type           = MapBackgroundType(r.JobType),
                   Status         = r.Status,
                   CreatedAt      = r.CreatedAt,
                   StartedAt      = r.StartedAt,
                   CompletedAt    = r.CompletedAt,
                   LibraryId      = r.LibraryId,
                   Version        = r.Version,
                   RenameToId     = renameTo,
                   ScanPath       = scanPath,
                   ItemsProcessed = r.ItemsProcessed,
                   ItemsTotal     = r.ItemsTotal,
                   ItemsLabel     = r.ItemsLabel,
                   ErrorCount     = 0,
                   ErrorMessage   = r.ErrorMessage
               };
}

private static JobType MapBackgroundType(string jobType)
{
    JobType result = JobType.Rechunk;
    switch (jobType)
    {
        case BackgroundJobTypes.Rechunk:                  result = JobType.Rechunk; break;
        case BackgroundJobTypes.RenameLibrary:            result = JobType.RenameLibrary; break;
        case BackgroundJobTypes.DeleteVersion:            result = JobType.DeleteVersion; break;
        case BackgroundJobTypes.DeleteLibrary:            result = JobType.DeleteLibrary; break;
        case BackgroundJobTypes.DryRunScrape:             result = JobType.DryRunScrape; break;
        case BackgroundJobTypes.IndexProjectDependencies: result = JobType.IndexProjectDependencies; break;
        case BackgroundJobTypes.SubmitUrlCorrection:      result = JobType.SubmitUrlCorrection; break;
    }
    return result;
}

private static (string? RenameTo, string? ScanPath) ParseInputJson(BackgroundJobRecord r)
{
    string? renameTo = null;
    string? scanPath = null;
    if (!string.IsNullOrEmpty(r.InputJson)
     && (r.JobType == BackgroundJobTypes.RenameLibrary
      || r.JobType == BackgroundJobTypes.IndexProjectDependencies))
    {
        try
        {
            using var doc = JsonDocument.Parse(r.InputJson);
            if (r.JobType == BackgroundJobTypes.RenameLibrary
             && doc.RootElement.TryGetProperty("newId", out var newIdEl))
            {
                renameTo = newIdEl.GetString();
            }
            if (r.JobType == BackgroundJobTypes.IndexProjectDependencies
             && doc.RootElement.TryGetProperty("path", out var pathEl))
            {
                scanPath = pathEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed input json — leave both null.
        }
    }
    return (renameTo, scanPath);
}
```

- [ ] **Step 8: Run the dryrun test — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests.DryRunBackgroundJobProjects"`
Expected: PASS.

- [ ] **Step 9: Add a failing test for rescrub projection**

Append to `UnifiedJobViewTests`:

```csharp
[Fact]
public async Task RescrubJobProjectsToRescrubRow()
{
    var rescrub = new FakeRescrubJobRepository();
    rescrub.Add(new RescrubJobRecord
                    {
                        Id = "r1",
                        LibraryId = "foo",
                        Version = "1.0",
                        Status = ScrapeJobStatus.Running,
                        CreatedAt = DateTime.UtcNow,
                        ChunksProcessed = 100,
                        ChunksTotal = 200
                    }
               );

    var view = new UnifiedJobView(new FakeScrapeJobRepository(), new FakeBackgroundJobRepository(), rescrub);
    var rows = await view.ListAsync(null, null, null, 10);

    var row = Assert.Single(rows);
    Assert.Equal(JobType.Rescrub, row.Type);
    Assert.Equal("foo", row.LibraryId);
    Assert.Equal(100, row.ItemsProcessed);
    Assert.Equal(200, row.ItemsTotal);
    Assert.Equal("chunks", row.ItemsLabel);
}
```

- [ ] **Step 10: Run it — fails**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests.RescrubJobProjects"`
Expected: FAIL — list empty.

- [ ] **Step 11: Add rescrub projection**

In `UnifiedJobView.ListAsync`, extend rows to all three:

```csharp
IEnumerable<JobRow> rows = scrapeTask.Result.Select(ProjectScrape)
                                            .Concat(backgroundTask.Result.Select(ProjectBackground))
                                            .Concat(rescrubTask.Result.Select(ProjectRescrub));
```

Update `GetAsync` to also check rescrub:

```csharp
if (result is null)
{
    var rs = await mRescrub.GetAsync(jobId, ct);
    if (rs is not null)
        result = ProjectRescrub(rs);
}
```

Add the projection method:

```csharp
private static JobRow ProjectRescrub(RescrubJobRecord r) => new JobRow
                                                                {
                                                                    JobId          = r.Id,
                                                                    Type           = JobType.Rescrub,
                                                                    Status         = r.Status,
                                                                    CreatedAt      = r.CreatedAt,
                                                                    StartedAt      = r.StartedAt,
                                                                    CompletedAt    = r.CompletedAt,
                                                                    LibraryId      = r.LibraryId,
                                                                    Version        = r.Version,
                                                                    ItemsProcessed = r.ChunksProcessed,
                                                                    ItemsTotal     = r.ChunksTotal,
                                                                    ItemsLabel     = "chunks",
                                                                    ErrorCount     = 0,
                                                                    ErrorMessage   = r.ErrorMessage
                                                                };
```

> If `RescrubJobRecord` exposes different property names (e.g. not `ChunksProcessed`/`ChunksTotal`/`ErrorMessage`), adjust to match. Read `SaddleRAG.Core/Models/RescrubJobRecord.cs` first if compile errors mention missing members.

- [ ] **Step 12: Run all `UnifiedJobViewTests`**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests"`
Expected: All three pass.

- [ ] **Step 13: Commit**

```bash
git add SaddleRAG.Monitor/Services/UnifiedJobView.cs SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs
git commit -F - <<'EOF'
feat(monitor): UnifiedJobView projects scrape, background, rescrub rows

Single read-side service that fans out to three job repositories,
projects each record into JobRow, and applies filters in memory.
InputJson parsing extracts newId for rename and path for deps-index;
malformed JSON degrades to nulls.
EOF
```

---

## Task 9: TDD `UnifiedJobView` — sort, filter, limit

**Files:**
- Modify: `SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs`

- [ ] **Step 1: Add a sort+limit test**

Append to `UnifiedJobViewTests`:

```csharp
[Fact]
public async Task ListSortsByCreatedAtDescAndAppliesLimit()
{
    var scrape = new FakeScrapeJobRepository();
    var bg = new FakeBackgroundJobRepository();
    var rescrub = new FakeRescrubJobRepository();
    var now = DateTime.UtcNow;

    scrape.Add(MakeScrape("s_old", now.AddMinutes(-30)));
    bg.Add(MakeBackground("b_mid", BackgroundJobTypes.Rechunk, now.AddMinutes(-20)));
    rescrub.Add(MakeRescrub("r_new", now.AddMinutes(-10)));

    var view = new UnifiedJobView(scrape, bg, rescrub);
    var rows = await view.ListAsync(null, null, null, limit: 2);

    Assert.Equal(2, rows.Count);
    Assert.Equal("r_new", rows[0].JobId);
    Assert.Equal("b_mid", rows[1].JobId);
}

private static ScrapeJobRecord MakeScrape(string id, DateTime createdAt) =>
    new ScrapeJobRecord
        {
            Id = id,
            Job = new ScrapeJob { LibraryId = "x", Version = "1", RootUrl = "https://x" },
            Status = ScrapeJobStatus.Completed,
            CreatedAt = createdAt
        };

private static BackgroundJobRecord MakeBackground(string id, string jobType, DateTime createdAt) =>
    new BackgroundJobRecord
        {
            Id = id,
            JobType = jobType,
            LibraryId = "x",
            Version = "1",
            InputJson = "{}",
            Status = ScrapeJobStatus.Completed,
            CreatedAt = createdAt
        };

private static RescrubJobRecord MakeRescrub(string id, DateTime createdAt) =>
    new RescrubJobRecord
        {
            Id = id,
            LibraryId = "x",
            Version = "1",
            Status = ScrapeJobStatus.Completed,
            CreatedAt = createdAt
        };
```

- [ ] **Step 2: Run it — should pass already**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ListSortsByCreatedAtDesc"`
Expected: PASS (`UnifiedJobView` already orders + limits).

- [ ] **Step 3: Add a status filter test**

```csharp
[Fact]
public async Task ListFiltersByStatus()
{
    var scrape = new FakeScrapeJobRepository();
    scrape.Add(MakeScrape("a", DateTime.UtcNow) with { Status = ScrapeJobStatus.Completed });
    scrape.Add(MakeScrape("b", DateTime.UtcNow.AddSeconds(-1)) with { Status = ScrapeJobStatus.Failed });

    var view = new UnifiedJobView(scrape, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository());
    var rows = await view.ListAsync(statusFilter: ScrapeJobStatus.Failed, null, null, limit: 10);

    var row = Assert.Single(rows);
    Assert.Equal("b", row.JobId);
}
```

> `ScrapeJobRecord` may not be a record (`with`-expression won't compile if it's a class). If so, build the second instance the long way:
> ```csharp
> scrape.Add(new ScrapeJobRecord { Id = "b", Job = new ScrapeJob { LibraryId = "x", Version = "1", RootUrl = "https://x" }, Status = ScrapeJobStatus.Failed, CreatedAt = DateTime.UtcNow.AddSeconds(-1) });
> ```

- [ ] **Step 4: Run it**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ListFiltersByStatus"`
Expected: PASS.

- [ ] **Step 5: Add a type filter test**

```csharp
[Fact]
public async Task ListFiltersByType()
{
    var scrape = new FakeScrapeJobRepository();
    scrape.Add(MakeScrape("s", DateTime.UtcNow));
    var bg = new FakeBackgroundJobRepository();
    bg.Add(MakeBackground("b", BackgroundJobTypes.Rechunk, DateTime.UtcNow));

    var view = new UnifiedJobView(scrape, bg, new FakeRescrubJobRepository());
    var rows = await view.ListAsync(null, typeFilter: JobType.Rechunk, null, limit: 10);

    var row = Assert.Single(rows);
    Assert.Equal("b", row.JobId);
}
```

- [ ] **Step 6: Run it**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~ListFiltersByType"`
Expected: PASS.

- [ ] **Step 7: Add a library substring filter test (verifies rows without library are excluded)**

```csharp
[Fact]
public async Task LibraryFilterExcludesRowsWithNoLibrary()
{
    var bg = new FakeBackgroundJobRepository();
    var withLib = MakeBackground("b1", BackgroundJobTypes.Rechunk, DateTime.UtcNow);
    var noLib = new BackgroundJobRecord
                    {
                        Id = "b2",
                        JobType = BackgroundJobTypes.RenameLibrary,
                        LibraryId = null,
                        Version = null,
                        InputJson = "{}",
                        Status = ScrapeJobStatus.Completed,
                        CreatedAt = DateTime.UtcNow.AddSeconds(-1)
                    };
    bg.Add(withLib);
    bg.Add(noLib);

    var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
    var rows = await view.ListAsync(null, null, libraryFilter: "x", limit: 10);

    var row = Assert.Single(rows);
    Assert.Equal("b1", row.JobId);
}
```

- [ ] **Step 8: Run it — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~LibraryFilterExcludes"`
Expected: PASS.

- [ ] **Step 9: Add a malformed-input-json safety test**

```csharp
[Fact]
public async Task MalformedInputJsonDoesNotCrashProjection()
{
    var bg = new FakeBackgroundJobRepository();
    bg.Add(new BackgroundJobRecord
               {
                   Id = "b1",
                   JobType = BackgroundJobTypes.RenameLibrary,
                   InputJson = "{not-json",
                   Status = ScrapeJobStatus.Completed,
                   CreatedAt = DateTime.UtcNow
               }
          );

    var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
    var rows = await view.ListAsync(null, null, null, 10);

    var row = Assert.Single(rows);
    Assert.Null(row.RenameToId);
}
```

- [ ] **Step 10: Run it — should pass**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MalformedInputJson"`
Expected: PASS.

- [ ] **Step 11: Run the full UnifiedJobViewTests**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~UnifiedJobViewTests"`
Expected: All pass.

- [ ] **Step 12: Commit**

```bash
git add SaddleRAG.Tests/Monitor/UnifiedJobViewTests.cs
git commit -F - <<'EOF'
test(monitor): cover UnifiedJobView sort, filter, and limit semantics
EOF
```

---

## Task 10: Wire `BackgroundJobRunner` to the broadcaster

**Files:**
- Modify: `SaddleRAG.Ingestion/BackgroundJobRunner.cs`
- Create: `SaddleRAG.Tests/Ingestion/BackgroundJobRunnerBroadcasterTests.cs`

This adds JobStarted at the top of `RunJobAsync`, JobProgress wrapped around `onProgress`, and the matching terminal event in each catch/success branch.

- [ ] **Step 1: Update `BackgroundJobRunner` constructor and fields**

In `SaddleRAG.Ingestion/BackgroundJobRunner.cs`, add the broadcaster dependency:

```csharp
public BackgroundJobRunner(RepositoryFactory repositoryFactory,
                           IMonitorBroadcaster broadcaster,
                           IHostApplicationLifetime lifetime,
                           ILogger<BackgroundJobRunner> logger)
{
    ArgumentNullException.ThrowIfNull(broadcaster);
    mRepositoryFactory = repositoryFactory;
    mBroadcaster = broadcaster;
    mAppStoppingToken = lifetime.ApplicationStopping;
    mLogger = logger;
}

private readonly IMonitorBroadcaster mBroadcaster;
```

Add the corresponding `using SaddleRAG.Core.Interfaces;` at the top if not already present.

- [ ] **Step 2: Wrap `RunJobAsync` with broadcaster lifecycle calls**

Replace the body of `RunJobAsync` with:

```csharp
private async Task RunJobAsync(BackgroundJobRecord jobRecord,
                               Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute)
{
    var jobRepo = mRepositoryFactory.GetBackgroundJobRepository(jobRecord.Profile);

    jobRecord.Status = ScrapeJobStatus.Running;
    jobRecord.StartedAt = DateTime.UtcNow;
    jobRecord.PipelineState = PipelineStateRunning;
    await jobRepo.UpsertAsync(jobRecord);

    mBroadcaster.RecordJobStarted(jobRecord.Id,
                                  jobRecord.LibraryId ?? string.Empty,
                                  jobRecord.Version ?? string.Empty,
                                  rootUrl: string.Empty
                                 );

    mLogger.LogInformation("Running background job {JobId} ({JobType}) for {LibraryId}/{Version}",
                           jobRecord.Id, jobRecord.JobType, jobRecord.LibraryId, jobRecord.Version);

    Action<int, int> onProgress = (processed, total) =>
                                  {
                                      jobRecord.ItemsProcessed = processed;
                                      jobRecord.ItemsTotal = total;
                                      jobRecord.LastProgressAt = DateTime.UtcNow;
                                      jobRepo.UpsertAsync(jobRecord).GetAwaiter().GetResult();
                                      mBroadcaster.RecordJobProgress(jobRecord.Id,
                                                                     processed,
                                                                     total,
                                                                     jobRecord.ItemsLabel ?? string.Empty
                                                                    );
                                  };

    try
    {
        await execute(jobRecord, onProgress, mAppStoppingToken);

        jobRecord.Status = ScrapeJobStatus.Completed;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCompleted(jobRecord.Id, indexedPageCount: 0);

        mLogger.LogInformation("Background job {JobId} ({JobType}) completed",
                               jobRecord.Id, jobRecord.JobType);
    }
    catch (OperationCanceledException)
    {
        mLogger.LogInformation("Background job {JobId} ({JobType}) was cancelled",
                               jobRecord.Id, jobRecord.JobType);

        jobRecord.Status = ScrapeJobStatus.Cancelled;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
        jobRecord.CancelledAt = DateTime.UtcNow;
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCancelled(jobRecord.Id);
    }
    catch (Exception ex)
    {
        mLogger.LogError(ex, "Background job {JobId} ({JobType}) failed", jobRecord.Id, jobRecord.JobType);

        jobRecord.Status = ScrapeJobStatus.Failed;
        jobRecord.ErrorMessage = ex.Message;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobFailed(jobRecord.Id, ex.Message);
    }
}
```

> Note: `RecordJobProgress` requires a non-empty label. Binary jobs leave `ItemsLabel` null and never call `onProgress`, so the empty-string fallback is never actually invoked. If `RecordJobProgress` somehow gets called with an empty label it will throw — that throw signals a bug in the runner contract and shouldn't be silenced.

- [ ] **Step 3: Update DI registration in `SaddleRAG.Mcp/Program.cs`**

Find the existing `services.AddSingleton<...BackgroundJobRunner...>(...)` registration. The constructor now requires `IMonitorBroadcaster`. Confirm `IMonitorBroadcaster` is already a registered singleton (it must be — `IngestionOrchestrator` already depends on it). DI will satisfy the new parameter automatically. No code change is needed here unless the registration is non-default; if so, add the broadcaster argument.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Write the broadcaster integration test**

Create `SaddleRAG.Tests/Ingestion/BackgroundJobRunnerBroadcasterTests.cs`:

```csharp
// BackgroundJobRunnerBroadcasterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Tests.Monitor;

#endregion

namespace SaddleRAG.Tests.Ingestion;

public sealed class BackgroundJobRunnerBroadcasterTests
{
    [Fact]
    public async Task BinaryJobEmitsStartedAndCompleted()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.RenameLibrary);
        await runner.QueueAsync(record, (_, _, _) => Task.CompletedTask);
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobStarted(record.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        broadcaster.Received(1).RecordJobCompleted(record.Id, Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CountedJobEmitsProgressForEachOnProgressCall()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.Rechunk);
        record.ItemsLabel = "chunks";

        await runner.QueueAsync(record, async (_, onProgress, _) =>
        {
            onProgress!(10, 100);
            onProgress(20, 100);
            await Task.CompletedTask;
        });
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobProgress(record.Id, 10, 100, "chunks");
        broadcaster.Received(1).RecordJobProgress(record.Id, 20, 100, "chunks");
    }

    [Fact]
    public async Task FailedJobEmitsFailedTerminalEvent()
    {
        var (runner, broadcaster, _) = MakeRunner();

        var record = MakeRecord(BackgroundJobTypes.DeleteVersion);
        await runner.QueueAsync(record, (_, _, _) => throw new InvalidOperationException("boom"));
        await WaitForCompletion(record, broadcaster);

        broadcaster.Received(1).RecordJobFailed(record.Id, "boom");
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
    }

    private static (BackgroundJobRunner runner,
                    IMonitorBroadcaster broadcaster,
                    FakeBackgroundJobRepository jobRepo) MakeRunner()
    {
        var jobRepo = new FakeBackgroundJobRepository();
        var factory = Substitute.For<RepositoryFactory>(args: Array.Empty<object>());
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = new BackgroundJobRunner(factory, broadcaster, lifetime, NullLogger<BackgroundJobRunner>.Instance);
        return (runner, broadcaster, jobRepo);
    }

    private static BackgroundJobRecord MakeRecord(string jobType) =>
        new BackgroundJobRecord
            {
                Id = Guid.NewGuid().ToString(),
                JobType = jobType,
                LibraryId = "lib",
                Version = "1",
                InputJson = "{}"
            };

    private static async Task WaitForCompletion(BackgroundJobRecord record, IMonitorBroadcaster broadcaster)
    {
        // Fire-and-forget pattern; poll briefly until the broadcaster has received a terminal event.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var completed = broadcaster.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "RecordJobCompleted"
                                                              or "RecordJobFailed"
                                                              or "RecordJobCancelled");
            if (completed) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Job {record.Id} did not reach a terminal event within 5s");
    }
}
```

> If `RepositoryFactory` cannot be substituted with `Substitute.For<RepositoryFactory>(args: Array.Empty<object>())` (e.g., it has a non-virtual constructor or no public parameterless ctor), check `RepositoryFactory.cs` for the actual signature and adapt the substitute creation. Existing test files (`HealthToolsTests`, `MutationToolsTests`) demonstrate the project's pattern for this — copy from there.

- [ ] **Step 6: Run the new tests**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~BackgroundJobRunnerBroadcasterTests"`
Expected: All three pass.

- [ ] **Step 7: Run the full test suite — confirm no regressions**

Run: `dotnet test SaddleRAG.Tests`
Expected: All pass.

- [ ] **Step 8: Commit**

```bash
git add SaddleRAG.Ingestion/BackgroundJobRunner.cs SaddleRAG.Tests/Ingestion/BackgroundJobRunnerBroadcasterTests.cs
git commit -F - <<'EOF'
feat(monitor): broadcast lifecycle events from BackgroundJobRunner

Every background job (rechunk, rename, delete-*, deps-index,
url-correction, dryrun-bookkeeping) now emits JobStarted, optional
JobProgress, and one of JobCompleted/Failed/Cancelled through the
shared broadcaster. The dryrun pipeline gets its own crawler-side
plumbing in a follow-up commit.
EOF
```

---

## Task 11: Wire `RescrubJobRunner` to the broadcaster

**Files:**
- Modify: `SaddleRAG.Ingestion/RescrubJobRunner.cs`

- [ ] **Step 1: Read the runner to see its progress callback shape**

Run: `git show HEAD:SaddleRAG.Ingestion/RescrubJobRunner.cs | head -100`

Find the spots where the runner sets `Status = Running`, calls back with progress counts, and the success/failure/cancel branches.

- [ ] **Step 2: Add `IMonitorBroadcaster` to the constructor**

Pattern matches Task 10 — accept `IMonitorBroadcaster`, store as `mBroadcaster`, add `using SaddleRAG.Core.Interfaces;` if needed.

- [ ] **Step 3: Around the lifecycle transitions, add broadcaster calls**

- After setting status to Running and persisting:
  ```csharp
  mBroadcaster.RecordJobStarted(record.Id, record.LibraryId, record.Version, rootUrl: string.Empty);
  ```
- In the progress callback (the rescrub already has one), after the DB upsert:
  ```csharp
  mBroadcaster.RecordJobProgress(record.Id, processed, total, "chunks");
  ```
- In the completed branch:
  ```csharp
  mBroadcaster.RecordJobCompleted(record.Id, indexedPageCount: 0);
  ```
- In the failed branch:
  ```csharp
  mBroadcaster.RecordJobFailed(record.Id, ex.Message);
  ```
- In the cancelled branch:
  ```csharp
  mBroadcaster.RecordJobCancelled(record.Id);
  ```

> If the runner doesn't have a progress callback today, locate the `chunksProcessed`/`chunksTotal` mutation site and call `RecordJobProgress` adjacent to it.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Run the existing rescrub tests, plus the broadcaster tests**

Run: `dotnet test SaddleRAG.Tests`
Expected: All pass.

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Ingestion/RescrubJobRunner.cs
git commit -F - <<'EOF'
feat(monitor): broadcast lifecycle and progress from RescrubJobRunner

Rescrub jobs now appear in the live monitor stream alongside scrapes
and the other background jobs.
EOF
```

---

## Task 12: Wire dry-run path to the broadcaster

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/IngestionTools.cs`

The dry-run path wraps `crawler.DryRunAsync` inside the `BackgroundJobRunner.QueueAsync` callback. The runner already handles JobStarted/Completed/Failed/Cancelled at the outer level (Task 10), so we don't double-emit. The crawler underneath already records fetch/reject events via the broadcaster when given a job id — no change needed there.

What we DO add: a final `BroadcastTick` call right before completing, so the row's last-fetched URLs land in the snapshot for the detail page.

- [ ] **Step 1: Read the current dry-run handler around line 117**

Run: `git show HEAD:SaddleRAG.Mcp/Tools/IngestionTools.cs | sed -n '95,140p'`

- [ ] **Step 2: Add a final tick after `crawler.DryRunAsync`**

In the `execute` lambda passed to `QueueAsync`, after the `crawler.DryRunAsync(...)` call returns and you've written the result JSON, add:

```csharp
mBroadcaster.BroadcastTick(record.Id);
```

(`mBroadcaster` is the existing field on `IngestionTools`. If not already injected, add it the same way the other dependencies are injected — check the constructor.)

> If `IngestionTools` is a static class, the broadcaster is passed in by parameter on each tool method. Use the parameter form there: `[FromServices] IMonitorBroadcaster broadcaster` then `broadcaster.BroadcastTick(record.Id);`.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Manually verify by running a dry-run end-to-end**

Smoke test only — no automated test. Run a small dry-run via the MCP tool, then load `/monitor/jobs` and confirm the dry-run row is present and its detail page shows recent fetches.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Mcp/Tools/IngestionTools.cs
git commit -F - <<'EOF'
feat(monitor): tick broadcaster after dryrun completes

Ensures the final fetch/reject snapshot reaches subscribers when
the dry-run runner finishes. JobStarted/Completed/Failed lifecycle
events come from BackgroundJobRunner; the crawler emits per-URL
fetch/reject inside DryRunAsync.
EOF
```

---

## Task 13: Replace `MonitorJobService` body with `IUnifiedJobView` delegation

**Files:**
- Modify: `SaddleRAG.Monitor/Services/MonitorJobService.cs`
- Modify: `SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs`

The existing `JobHistoryRow` type stays — the page already binds to it. We change the field, constructor argument, and projection source.

- [ ] **Step 1: Update existing tests to use the unified view fake**

Open `SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs`. Wherever the test instantiates `MonitorJobService(scrapeRepoFake)`, change it to `MonitorJobService(new UnifiedJobView(scrapeRepoFake, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository()))`. The existing assertions about scrape rows continue to apply because the view passes scrape data through.

- [ ] **Step 2: Run tests to verify they fail at the constructor**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorJobServiceTests"`
Expected: Compile error — `MonitorJobService` constructor still takes `IScrapeJobRepository`.

- [ ] **Step 3: Rewrite `MonitorJobService` to use `IUnifiedJobView`**

Replace `SaddleRAG.Monitor/Services/MonitorJobService.cs`:

```csharp
// MonitorJobService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Read-side service for the /monitor/jobs index page. Wraps
///     <see cref="IUnifiedJobView" /> and projects <see cref="JobRow" />
///     into the page-friendly <see cref="JobHistoryRow" /> shape.
/// </summary>
public sealed class MonitorJobService
{
    public MonitorJobService(IUnifiedJobView jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        mJobs = jobs;
    }

    private readonly IUnifiedJobView mJobs;

    public sealed record JobHistoryRow
    {
        public required string JobId { get; init; }
        public required JobType Type { get; init; }
        public string? LibraryId { get; init; }
        public string? Version { get; init; }
        public string? RenameToId { get; init; }
        public string? ScanPath { get; init; }
        public required string Status { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int ItemsProcessed { get; init; }
        public int ItemsTotal { get; init; }
        public string? ItemsLabel { get; init; }
        public int ErrorCount { get; init; }
        public string? ErrorMessage { get; init; }

        public TimeSpan? Duration => StartedAt is null
                                         ? null
                                         : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
    }

    public async Task<IReadOnlyList<JobHistoryRow>> ListAsync(ScrapeJobStatus? status = null,
                                                              JobType? typeFilter = null,
                                                              string? libraryIdFilter = null,
                                                              int limit = DefaultLimit,
                                                              CancellationToken ct = default)
    {
        var rows = await mJobs.ListAsync(status, typeFilter, libraryIdFilter, limit, ct);
        return rows.Select(r => new JobHistoryRow
                                    {
                                        JobId          = r.JobId,
                                        Type           = r.Type,
                                        LibraryId      = r.LibraryId,
                                        Version        = r.Version,
                                        RenameToId     = r.RenameToId,
                                        ScanPath       = r.ScanPath,
                                        Status         = r.Status.ToString(),
                                        CreatedAt      = r.CreatedAt,
                                        StartedAt      = r.StartedAt,
                                        CompletedAt    = r.CompletedAt,
                                        ItemsProcessed = r.ItemsProcessed,
                                        ItemsTotal     = r.ItemsTotal,
                                        ItemsLabel     = r.ItemsLabel,
                                        ErrorCount     = r.ErrorCount,
                                        ErrorMessage   = r.ErrorMessage
                                    })
                   .ToList();
    }

    private const int DefaultLimit = 100;
}
```

- [ ] **Step 4: Run the updated MonitorJobServiceTests**

Run: `dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorJobServiceTests"`
Expected: All pass. Existing assertions on scrape rows continue to work because they look up `JobId`/`LibraryId`/`Status` which are still present.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Monitor/Services/MonitorJobService.cs SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs
git commit -F - <<'EOF'
feat(monitor): MonitorJobService delegates to IUnifiedJobView

The /monitor/jobs page now sees scrape, dryrun, rechunk, rescrub,
rename, delete, deps-index, and url-correction jobs in one merged
list. JobHistoryRow gains Type, RenameToId, ScanPath, ItemsLabel
fields. The existing Razor page bindings are unaffected; new fields
are surfaced in a follow-up UI commit.
EOF
```

---

## Task 14: Update `MonitorDataService` to use `IUnifiedJobView`

**Files:**
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs`

`GetJobInfoAsync` and `GetLatestJobIdAsync` currently call `IScrapeJobRepository`. Both move to `IUnifiedJobView`.

- [ ] **Step 1: Add `IUnifiedJobView` to the constructor**

```csharp
public MonitorDataService(ILibraryRepository libraries,
                          IChunkRepository chunks,
                          ILibraryProfileRepository profiles,
                          IUnifiedJobView jobs,
                          IScrapeAuditRepository audit)
{
    ArgumentNullException.ThrowIfNull(libraries);
    ArgumentNullException.ThrowIfNull(chunks);
    ArgumentNullException.ThrowIfNull(profiles);
    ArgumentNullException.ThrowIfNull(jobs);
    ArgumentNullException.ThrowIfNull(audit);
    mLibraries = libraries;
    mChunks = chunks;
    mProfiles = profiles;
    mJobs = jobs;
    mAudit = audit;
}

private readonly IUnifiedJobView mJobs;
```

(Replace the existing `IScrapeJobRepository` field/parameter.)

- [ ] **Step 2: Rewrite `GetLatestJobIdAsync`**

```csharp
public async Task<string?> GetLatestJobIdAsync(string libraryId,
                                               string version,
                                               CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);
    var rows = await mJobs.ListAsync(statusFilter: null,
                                     typeFilter: null,
                                     libraryFilter: libraryId,
                                     limit: RecentJobsScanLimit,
                                     ct);
    var match = rows.Where(r => string.Equals(r.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
    return match?.JobId;
}
```

- [ ] **Step 3: Rewrite `GetJobInfoAsync`**

```csharp
public async Task<JobInfo?> GetJobInfoAsync(string jobId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(jobId);
    JobInfo? result = null;
    var row = await mJobs.GetAsync(jobId, ct);
    if (row is not null)
    {
        result = new JobInfo
                     {
                         JobId = row.JobId,
                         LibraryId = row.LibraryId,
                         Version = row.Version,
                         Status = row.Status.ToString(),
                         StartedAt = row.StartedAt,
                         CompletedAt = row.CompletedAt,
                         ErrorMessage = row.ErrorMessage
                     };
    }
    return result;
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Run tests**

Run: `dotnet test SaddleRAG.Tests`
Expected: All pass. (Existing `MonitorDataServiceEnrichmentTests` need their constructor calls updated — replace `IScrapeJobRepository` arg with a fake `IUnifiedJobView`. If the test uses `Substitute.For<IScrapeJobRepository>`, swap it for `Substitute.For<IUnifiedJobView>` and configure with `.GetAsync(...)` returning the relevant `JobRow`.)

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Monitor/Services/MonitorDataService.cs SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs
git commit -F - <<'EOF'
feat(monitor): MonitorDataService routes through IUnifiedJobView

Per-job header lookups (job detail page) and latest-job-by-library
queries now resolve any job type, not just scrapes.
EOF
```

---

## Task 15: Register `IUnifiedJobView` in DI

**Files:**
- Modify: `SaddleRAG.Mcp/Program.cs`
- Modify: `SaddleRAG.Database/ServiceCollectionExtensions.cs` (only if needed for repository registration)

- [ ] **Step 1: Confirm `IBackgroundJobRepository` and `IRescrubJobRepository` are registered**

Run: `grep -rn "AddSingleton.*BackgroundJobRepository\|AddSingleton.*RescrubJobRepository" SaddleRAG.Database/ SaddleRAG.Mcp/`

If both are present, skip to Step 3. Otherwise add them in `ServiceCollectionExtensions.cs` alongside the existing repositories:

```csharp
services.AddSingleton<IBackgroundJobRepository, BackgroundJobRepository>();
services.AddSingleton<IRescrubJobRepository, RescrubJobRepository>();
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Register `IUnifiedJobView` in `Program.cs`**

In `SaddleRAG.Mcp/Program.cs`, near the existing `builder.Services.AddSingleton<MonitorJobService>();` line, add:

```csharp
builder.Services.AddSingleton<IUnifiedJobView, UnifiedJobView>();
```

Add `using SaddleRAG.Monitor.Services;` and `using SaddleRAG.Core.Interfaces;` if not already present.

- [ ] **Step 4: Build and run the full app**

Run: `dotnet build`
Expected: Build succeeded.

Run: `dotnet run --project SaddleRAG.Mcp`
Expected: Process starts without DI exceptions; `/monitor/jobs` loads. Stop with Ctrl+C.

- [ ] **Step 5: Commit**

```bash
git add SaddleRAG.Mcp/Program.cs SaddleRAG.Database/ServiceCollectionExtensions.cs
git commit -F - <<'EOF'
feat(monitor): register IUnifiedJobView in DI
EOF
```

---

## Task 16: Update `JobHistoryPage.razor.cs` with type filter

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/JobHistoryPage.razor.cs`

- [ ] **Step 1: Add the type filter property**

Add to the class body after `LibraryFilter`:

```csharp
/// <summary>
///     Selected job type filter or null for "all types".
/// </summary>
protected JobType? TypeFilter { get; set; }

protected static readonly JobType[] pmTypeChoices = Enum.GetValues<JobType>();
```

Add `using SaddleRAG.Core.Models.Monitor;` at the top if not already present.

- [ ] **Step 2: Pass `TypeFilter` through `LoadAsync`**

Replace the body of `LoadAsync`:

```csharp
protected async Task LoadAsync()
{
    ArgumentNullException.ThrowIfNull(Jobs);
    ScrapeJobStatus? statusEnum = null;
    if (!string.IsNullOrEmpty(StatusFilter)
     && Enum.TryParse<ScrapeJobStatus>(StatusFilter, ignoreCase: true, out var parsed))
    {
        statusEnum = parsed;
    }

    Rows = await Jobs.ListAsync(statusEnum, TypeFilter, LibraryFilter, LimitChoice);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add SaddleRAG.Monitor/Pages/JobHistoryPage.razor.cs
git commit -F - <<'EOF'
feat(monitor): add Type filter binding to JobHistoryPage code-behind
EOF
```

---

## Task 17: Update `JobHistoryPage.razor` with new column layout

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/JobHistoryPage.razor`

- [ ] **Step 1: Replace the page markup**

```razor
@* SaddleRAG.Monitor/Pages/JobHistoryPage.razor *@
@page "/monitor/jobs"
@rendermode InteractiveServer
@using SaddleRAG.Core.Models.Monitor
@using SaddleRAG.Monitor.Services
@inherits JobHistoryPageBase

<MudText Typo="Typo.h4" Class="mb-3">Jobs</MudText>

<MudStack Row="true" Spacing="2" Class="mb-3" Wrap="Wrap.Wrap">
    <MudSelect T="string" Label="Status" @bind-Value="StatusFilter" Clearable="true"
               Style="min-width:140px">
        @foreach (var s in pmStatusChoices)
        {
            <MudSelectItem Value="@s">@s</MudSelectItem>
        }
    </MudSelect>
    <MudSelect T="JobType?" Label="Type" @bind-Value="TypeFilter" Clearable="true"
               Style="min-width:160px">
        @foreach (var t in pmTypeChoices)
        {
            <MudSelectItem T="JobType?" Value="@((JobType?)t)">@TypeLabel(t)</MudSelectItem>
        }
    </MudSelect>
    <MudTextField T="string" Label="Library contains" @bind-Value="LibraryFilter" Immediate="true"/>
    <MudSelect T="int" Label="Limit" @bind-Value="LimitChoice" Style="min-width:100px">
        <MudSelectItem Value="50">50</MudSelectItem>
        <MudSelectItem Value="100">100</MudSelectItem>
        <MudSelectItem Value="500">500</MudSelectItem>
    </MudSelect>
    <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="LoadAsync">Apply</MudButton>
</MudStack>

<MudTable Items="@Rows" Dense="true" Hover="true" T="MonitorJobService.JobHistoryRow"
          OnRowClick="OnRowClicked">
    <HeaderContent>
        <MudTh>Created</MudTh>
        <MudTh>Type</MudTh>
        <MudTh>Target</MudTh>
        <MudTh>Status</MudTh>
        <MudTh>Progress</MudTh>
        <MudTh>Duration</MudTh>
        <MudTh>Job</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd><TimeDisplay Utc="@context.CreatedAt"/></MudTd>
        <MudTd>
            <MudChip T="string" Size="Size.Small" Color="@TypeColor(context.Type)">
                @TypeLabel(context.Type)
            </MudChip>
        </MudTd>
        <MudTd>@RenderTarget(context)</MudTd>
        <MudTd>
            <MudChip T="string" Size="Size.Small" Color="@StatusColor(context.Status)">
                @context.Status
            </MudChip>
        </MudTd>
        <MudTd>@RenderProgress(context)</MudTd>
        <MudTd>@(context.Duration?.ToString(@"hh\:mm\:ss") ?? "—")</MudTd>
        <MudTd Style="font-family:monospace; font-size:.7rem">@context.JobId</MudTd>
    </RowTemplate>
    <NoRecordsContent>
        <MudText>No jobs match the current filters.</MudText>
    </NoRecordsContent>
</MudTable>

@code {

    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    private static Color StatusColor(string status) => status switch
    {
        "Running"   => Color.Info,
        "Queued"    => Color.Default,
        "Completed" => Color.Success,
        "Failed"    => Color.Error,
        "Cancelled" => Color.Warning,
        var _       => Color.Default
    };

    private static Color TypeColor(JobType type) => type switch
    {
        JobType.Scrape                   => Color.Info,
        JobType.DryRunScrape             => Color.Info,
        JobType.IndexProjectDependencies => Color.Info,
        JobType.Rechunk                  => Color.Primary,
        JobType.Rescrub                  => Color.Primary,
        JobType.SubmitUrlCorrection      => Color.Primary,
        JobType.RenameLibrary            => Color.Warning,
        JobType.DeleteVersion            => Color.Warning,
        JobType.DeleteLibrary            => Color.Warning,
        var _                            => Color.Default
    };

    private static string TypeLabel(JobType type) => type switch
    {
        JobType.Scrape                   => "Scrape",
        JobType.DryRunScrape             => "Dry-run",
        JobType.Rechunk                  => "Rechunk",
        JobType.Rescrub                  => "Rescrub",
        JobType.RenameLibrary            => "Rename",
        JobType.DeleteVersion            => "Delete version",
        JobType.DeleteLibrary            => "Delete library",
        JobType.IndexProjectDependencies => "Index deps",
        JobType.SubmitUrlCorrection      => "URL fix",
        var _                            => type.ToString()
    };

    private static string RenderTarget(MonitorJobService.JobHistoryRow row) => row.Type switch
    {
        JobType.RenameLibrary
            => $"{row.LibraryId} → {row.RenameToId ?? "(unknown)"}",
        JobType.IndexProjectDependencies
            => $"(scan: {row.ScanPath ?? "(unknown)"})",
        JobType.DeleteLibrary
            => row.LibraryId ?? "(unknown)",
        var _ when string.IsNullOrEmpty(row.LibraryId)
            => "—",
        var _ when string.IsNullOrEmpty(row.Version)
            => row.LibraryId!,
        var _
            => $"{row.LibraryId} @ {row.Version}"
    };

    private static string RenderProgress(MonitorJobService.JobHistoryRow row)
    {
        var label = row.ItemsLabel;
        if (string.IsNullOrEmpty(label) || (row.ItemsProcessed == 0 && row.ItemsTotal == 0))
            return "—";
        if (row.ItemsTotal > 0)
            return $"{row.ItemsProcessed:N0} / {row.ItemsTotal:N0} {label}";
        return $"{row.ItemsProcessed:N0} {label}";
    }

    private void OnRowClicked(TableRowClickEventArgs<MonitorJobService.JobHistoryRow> args)
    {
        if (args.Item is not null)
            Nav.NavigateTo($"/monitor/jobs/{args.Item.JobId}");
    }

}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Smoke-test in the browser**

Run the app, navigate to `/monitor/jobs`. Verify: type chip shows for every row, target column renders rename/deps-index correctly when you have such jobs, progress column shows the right unit per row.

- [ ] **Step 4: Commit**

```bash
git add SaddleRAG.Monitor/Pages/JobHistoryPage.razor
git commit -F - <<'EOF'
feat(monitor): unified jobs page columns (Type, Target, Progress)

Replaces Library/Version/Indexed/Errors with type-aware columns. Each
job type renders the right Target shape (lib@ver, lib→newId, scan
path) and the right Progress unit (pages, chunks, packages, or —).
EOF
```

---

## Task 18: Make `JobDetailPage` graceful for non-scrape jobs

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs`

The detail page subscribes to live ticks and audit feeds. For non-scrape jobs there is no audit data — `MonitorDataService.GetAuditSummaryAsync` already returns null in that case, so we just need to make sure the Razor markup doesn't throw on null audit.

- [ ] **Step 1: Skim `JobDetailPage.razor.cs` and the `.razor`**

Run: `git show HEAD:SaddleRAG.Monitor/Pages/JobDetailPage.razor`

Identify any place that assumes audit is non-null or assumes the row is a scrape (e.g. crawler-specific labels).

- [ ] **Step 2: Wrap audit-dependent sections in `@if (mAuditSummary is not null)`**

Modify the `.razor` so the audit section, recent-fetches feed, and recent-rejects feed only render when `mAuditSummary` (or whatever the field is called) is non-null.

- [ ] **Step 3: Update header to show the job type**

Above the existing library/version display, add:

```razor
<MudChip T="string" Size="Size.Small" Color="@TypeColor(JobInfoRow.Type)">@TypeLabel(JobInfoRow.Type)</MudChip>
```

(Reuse the `TypeColor`/`TypeLabel` helpers from Task 17 — extract them to a shared static class `MonitorTypeFormatting` in `SaddleRAG.Monitor.Pages` if both pages need them.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Smoke-test**

Run the app, navigate to a known dry-run job's detail page. Verify the page loads without exception, header shows type, audit feeds appear (dry-run does have audit data) or absent (binary job).

Also navigate to a known rename or delete job's detail page. Verify the page loads without exception and the audit-dependent sections are hidden.

- [ ] **Step 6: Commit**

```bash
git add SaddleRAG.Monitor/Pages/JobDetailPage.razor SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs
git commit -F - <<'EOF'
feat(monitor): JobDetailPage handles non-scrape jobs

Audit-dependent sections now render conditionally; type chip in the
header makes the job type visible at a glance.
EOF
```

---

## Task 19: Final regression sweep

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 2: Smoke-test the full monitor flow**

Run: `dotnet run --project SaddleRAG.Mcp`

In the browser:
1. Open `/monitor/jobs`. Confirm scrape, dry-run, and any background jobs from the DB all appear with correct type chips and Target/Progress cells.
2. Apply each filter in turn (Status, Type, Library) and confirm rows narrow correctly.
3. Click into a scrape, a dry-run, and a binary-job (rename/delete if available) detail page. Confirm none throw.
4. Kick off a small dry-run via MCP. Confirm the row appears live in the list and the progress count ticks.

- [ ] **Step 3: If everything works, push the branch**

```bash
git push -u origin fix/jobs
```

- [ ] **Step 4: Open a draft PR for review**

```bash
gh pr create --draft --title "fix(monitor): unified jobs page" --body-file - <<'EOF'
## Summary
- Surface every job type — scrape, dry-run, rechunk, rescrub, rename, delete, deps-index, url-correction — on /monitor/jobs.
- Bring all job runners to live-update parity via the `MonitorBroadcaster`.
- New `IUnifiedJobView` service unions the three job repositories behind a single read-side seam.
- New `JobProgress` SignalR event for counted background jobs.

## Test plan
- [x] `dotnet test` — all green
- [ ] Run a fresh dry-run; row appears live in /monitor/jobs and ticks
- [ ] Run a small rechunk; row appears with chunks progress ticking
- [ ] Existing scrape monitoring continues unchanged
- [ ] Rename / delete jobs render with `—` progress and the right Target shape
EOF
```

---

## Self-Review

**Spec coverage check:**

- UX column layout (Created/Type/Target/Status/Progress/Duration/Job) — Task 17 ✅
- Per-type Target rendering (rename, deps-index, delete-library variants) — Task 17 ✅
- Type chip colors — Task 17 ✅
- Status / Type / Library filters — Tasks 16, 17 ✅
- `JobRow` record + `JobType` enum — Task 1 ✅
- `IUnifiedJobView` interface + impl — Tasks 6, 8, 9 ✅
- `MonitorJobService` rewrite — Task 13 ✅
- `MonitorDataService.GetJobInfoAsync` rewrite — Task 14 ✅
- `BackgroundJobRecord` InputJson parsing for rename + deps-index — Task 8 ✅
- `JobProgressEvent` + `RecordJobProgress` — Tasks 2, 3 ✅
- SignalR forwarding of `JobProgress` — Task 5 ✅
- Broadcaster guard relaxation — Task 4 ✅
- BackgroundJobRunner broadcaster integration (binary + counted) — Task 10 ✅
- RescrubJobRunner broadcaster integration — Task 11 ✅
- Dry-run path broadcaster integration — Task 12 ✅
- DI registration — Task 15 ✅
- Job detail page graceful for non-scrape — Task 18 ✅

**Tests covered:**
- `UnifiedJobViewTests` (new) — projection, sort, filter, malformed-json — Tasks 8, 9
- `BackgroundJobRunnerBroadcasterTests` (new) — Task 10
- `MonitorBroadcasterEventsTests` (extended) — Task 3 (`JobProgress`), Task 4 (empty-strings)
- `MonitorJobServiceTests` (updated) — Task 13
- `MonitorDataServiceEnrichmentTests` (updated) — Task 14
- Smoke tests for the UI in Tasks 17, 18, 19

**Placeholder scan:** No "TBD"/"TODO"/"similar to". Tasks 11, 12, 18 reference exact code patterns from earlier tasks (broadcaster-call ordering, audit-conditional render) — that's intentional cross-reference, not a placeholder.

**Type consistency:** `JobType`, `JobRow`, `JobProgressEvent`, and `JobHistoryRow` field names line up across tasks. `RecordJobProgress` signature `(jobId, processed, total, label)` is used identically in Tasks 3, 10, 11, and 12.

---

## Execution Handoff

Plan complete and saved to [`docs/superpowers/plans/2026-05-07-unified-jobs-page.md`](2026-05-07-unified-jobs-page.md). Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
