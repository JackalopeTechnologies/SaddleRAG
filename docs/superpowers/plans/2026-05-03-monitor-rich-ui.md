# SaddleRAG Monitor — Rich UI Completion Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the current `/monitor` scaffolding into a full operations console where a SaddleRAG operator can see every job that has ever run, examine each library in depth (counts, suspect reasons, profile/symbols, version history, audit summary), watch live jobs with rate deltas and final-state replay for terminal jobs, and act on libraries (rescrape, rescrub, delete) from the UI.

**Architecture:** Build out the gaps in the existing scaffolding from Wave 2. New work breaks into:
1. **Layout shell** — `MudLayout` + `MudAppBar` + `MudDrawer` wrapping every page; consistent theming.
2. **Live wiring fixes** — bind the active-jobs strip to SignalR payloads; push discrete lifecycle events to clients.
3. **Data plumbing** — `MonitorDataService` gains new query methods; new VM records carry `SuspectReasons`, `LastScrapedAt`, hostname distribution, language mix, boundary issue %, profile, version list, audit summary, job history.
4. **UI build-out** — every spec-listed component that is currently absent or stubbed.
5. **Write actions** — Rescrape / Rescrub / Delete-version buttons calling new `/api/monitor` endpoints behind the existing `DiagnosticsWrite` policy.
6. **Job history** — new `/monitor/jobs` index page with filters + pagination (NOT in original spec; addresses repeated user request).

**Tech Stack:** C# / .NET 10, MudBlazor 8.x, Blazor Server, SignalR, MongoDB.Driver, xUnit, bUnit. All commits land on `feature/scrape-diagnostics-monitor` (current branch). Master only via PR.

---

## Spec & audit references

- **Original design:** [`docs/superpowers/specs/2026-05-02-scrape-diagnostics-monitor-design.md`](../specs/2026-05-02-scrape-diagnostics-monitor-design.md)
- **Wave 2 plan (partial, predates this plan):** [`docs/superpowers/plans/2026-05-03-scrape-diagnostics-wave2-monitor.md`](2026-05-03-scrape-diagnostics-wave2-monitor.md)
- **Audit driving this plan:** the 2026-05-03 review identified that the as-shipped monitor is roughly Wave 1 scaffolding — pages mount, but lifecycle binding is broken, suspect reasons aren't surfaced, library detail is two scalar counters, profile/versions/job-history are absent, no app-shell.

---

## Conventions (apply to every new file)

1. **File header:**
   ```csharp
   // FileName.cs
   // Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
   // SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
   // Available under AGPLv3 (see LICENSE) or a commercial license
   // (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.
   ```
2. `#region Usings` block immediately under the header.
3. Single namespace per file, mirroring folder structure.
4. **Allman braces; 4-space indent; max 120 char lines.**
5. Field prefixes: `m` (instance), `ps` (private static), `sm` (static readonly), `pm` (public instance). Constants: PascalCase.
6. **No early returns** — use the variable pattern.  **No `continue`** — use `Where` or if-block.  **No if/else chains** — switch expressions or pattern matching.
7. Tests: xUnit `[Fact]`, `Assert.*`, namespace mirrors source folder under `SaddleRAG.Tests`.
8. Build: `dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true` (no quotes around `-p:`).
9. **Commit messages must be passed via a file:** never inline. Write to `msg.txt`, then `git commit -F msg.txt`. NEVER add a `Co-Authored-By:` trailer or `🤖 Generated with Claude Code` line.
10. Razor: any `@expression` immediately preceded by an alphanumeric must use parens: `v@(Detail.Version)` not `v@Detail.Version`.

---

## Out of scope for this plan

These are real gaps but each requires separate design work; calling them out so reviewers don't ask "where's pause?":

- **Pause / resume of running jobs.** Spec mentions `Pause` buttons; the orchestrator has no pause point. Needs an orchestrator-level mechanism (cancellation-token-pair, suspend channel, or queue-quiescing primitive). Tracked separately.
- **Multi-token write auth UI.** Spec keeps single shared bearer token configuration-only.
- **Live throughput sparkline charts.** Per-stage rate is in scope; per-stage time-series charts are not.

---

## File map (created or modified by this plan)

### New files

```
SaddleRAG.Mcp/Monitor/MainLayout.razor                       (Phase 1)
SaddleRAG.Mcp/Monitor/MainLayout.razor.cs                    (Phase 1)
SaddleRAG.Monitor/Components/SuspectChip.razor               (Phase 2)
SaddleRAG.Monitor/Components/HostnameDistribution.razor      (Phase 4)
SaddleRAG.Monitor/Components/LanguageMix.razor               (Phase 4)
SaddleRAG.Monitor/Components/AuditSummaryPanel.razor         (Phase 4)
SaddleRAG.Monitor/Components/VersionList.razor               (Phase 4)
SaddleRAG.Monitor/Components/ProfileView.razor               (Phase 4)
SaddleRAG.Monitor/Components/JobHistoryRow.razor             (Phase 6)
SaddleRAG.Monitor/Components/RatePipelineStrip.razor         (Phase 5)
SaddleRAG.Monitor/Components/ErrorList.razor                 (Phase 5)
SaddleRAG.Monitor/Pages/JobHistoryPage.razor                 (Phase 6)
SaddleRAG.Monitor/Pages/JobHistoryPage.razor.cs              (Phase 6)
SaddleRAG.Monitor/Services/MonitorJobService.cs              (Phase 6)
SaddleRAG.Monitor/Services/RatesAccumulator.cs               (Phase 5)
SaddleRAG.Mcp/Api/MonitorLibraryActionsEndpoints.cs          (Phase 7)
SaddleRAG.Tests/Monitor/MainLayoutTests.cs                   (Phase 1)
SaddleRAG.Tests/Monitor/LandingPageActiveJobsTests.cs        (Phase 2)
SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs (Phase 3)
SaddleRAG.Tests/Monitor/RatesAccumulatorTests.cs             (Phase 5)
SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs            (Phase 6)
SaddleRAG.Tests/Monitor/JobHistoryPageTests.cs               (Phase 6)
SaddleRAG.Tests/Monitor/MonitorLibraryActionsEndpointsTests.cs (Phase 7)
SaddleRAG.Core/Interfaces/IQueryMetrics.cs                   (Phase 9)
SaddleRAG.Core/Models/Monitor/QuerySample.cs                 (Phase 9)
SaddleRAG.Core/Models/Monitor/QueryMetricsSnapshot.cs        (Phase 9)
SaddleRAG.Ingestion/Diagnostics/QueryMetricsRecorder.cs      (Phase 9)
SaddleRAG.Ingestion/Diagnostics/QueryMetricsExtensions.cs    (Phase 9)
SaddleRAG.Monitor/Pages/PerformancePage.razor                (Phase 9)
SaddleRAG.Monitor/Pages/PerformancePage.razor.cs             (Phase 9)
SaddleRAG.Tests/Monitor/QueryMetricsRecorderTests.cs         (Phase 9)
SaddleRAG.Tests/Monitor/QueryMetricsExtensionsTests.cs       (Phase 9)
```

### Modified files

```
SaddleRAG.Mcp/Monitor/App.razor                              (theme + base href)
SaddleRAG.Mcp/Monitor/Routes.razor                           (default layout)
SaddleRAG.Mcp/Program.cs                                     (DI for new services + endpoints)
SaddleRAG.Monitor/Pages/LandingPage.razor                    (sort, suspect chip, empty state, AppBar awareness)
SaddleRAG.Monitor/Pages/LandingPage.razor.cs                 (active-jobs binding fix)
SaddleRAG.Monitor/Pages/LibraryDetailPage.razor              (rich tabs)
SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs           (data fetch fan-out)
SaddleRAG.Monitor/Pages/JobDetailPage.razor                  (header, errors list, terminal state)
SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs               (rate calc, terminal-state load)
SaddleRAG.Monitor/Pages/AuditInspectorPage.razor             (histogram, parent-url col, expand panel)
SaddleRAG.Monitor/Pages/LibrarySummaryItem.cs                (+ SuspectReasons, LastScrapedAt)
SaddleRAG.Monitor/Pages/LibraryDetailData.cs                 (rich shape)
SaddleRAG.Monitor/Components/LibraryCard.razor               (suspect chip with tooltip)
SaddleRAG.Monitor/Components/JobCardStrip.razor              (richer header, link to job detail)
SaddleRAG.Monitor/Components/PipelineStrip.razor             (replaced by RatePipelineStrip — see Phase 5)
SaddleRAG.Monitor/Services/MonitorDataService.cs             (enrichment query methods)
SaddleRAG.Mcp/Hubs/MonitorHub.cs                             (lifecycle event broadcasts)
SaddleRAG.Mcp/Hubs/MonitorTickService.cs                     (push discrete events)
SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs        (raise discrete events through hub context)
SaddleRAG.Mcp/Api/MonitorApiEndpoints.cs                     (snapshot read endpoints)
SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs             (event delegates)
```

---

## Phase 1 — Layout shell & navigation

The current `/monitor` page floats bare on the body — no app bar, no nav, no consistent chrome. Every other phase needs a shell to live inside, so this goes first.

### Task 1.1: Add `MainLayout` with `MudLayout` / `MudAppBar` / `MudDrawer`

**Files:**
- Create: `SaddleRAG.Mcp/Monitor/MainLayout.razor`
- Create: `SaddleRAG.Mcp/Monitor/MainLayout.razor.cs`
- Modify: `SaddleRAG.Mcp/Monitor/Routes.razor`
- Test: `SaddleRAG.Tests/Monitor/MainLayoutTests.cs`

- [ ] **Step 1: Add a bUnit test for layout shell rendering**

```csharp
// SaddleRAG.Tests/Monitor/MainLayoutTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Bunit;
using MudBlazor.Services;
using SaddleRAG.Mcp.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MainLayoutTests : BunitContext
{
    [Fact]
    public void MainLayoutRendersAppBarTitleAndNavLinks()
    {
        Services.AddMudServices();
        JSInterop.SetupVoid("mudPopover.initialize", _ => true);
        JSInterop.SetupVoid("mudKeyInterceptor.connect", _ => true);

        var cut = Render<MainLayout>(parameters => parameters
            .AddChildContent("<p>child</p>"));

        var markup = cut.Markup;
        Assert.Contains("SaddleRAG Monitor", markup);
        Assert.Contains("href=\"/monitor\"", markup);
        Assert.Contains("href=\"/monitor/jobs\"", markup);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MainLayoutTests"
```
Expected: FAIL — `MainLayout` type does not exist.

- [ ] **Step 3: Create `MainLayout.razor.cs`**

```csharp
// MainLayout.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;

#endregion

namespace SaddleRAG.Mcp.Monitor;

public abstract class MainLayoutBase : LayoutComponentBase
{
    protected bool DrawerOpen { get; set; } = true;

    protected void ToggleDrawer()
    {
        DrawerOpen = !DrawerOpen;
    }
}
```

- [ ] **Step 4: Create `MainLayout.razor`**

```razor
@* SaddleRAG.Mcp/Monitor/MainLayout.razor *@
@inherits MainLayoutBase

<MudLayout>
    <MudAppBar Elevation="1" Color="Color.Primary">
        <MudIconButton Icon="@Icons.Material.Filled.Menu" Color="Color.Inherit"
                       OnClick="@ToggleDrawer"/>
        <MudText Typo="Typo.h6" Class="ml-2">SaddleRAG Monitor</MudText>
        <MudSpacer/>
        <MudLink Href="/monitor" Color="Color.Inherit" Class="mx-2">Libraries</MudLink>
        <MudLink Href="/monitor/jobs" Color="Color.Inherit" Class="mx-2">Jobs</MudLink>
    </MudAppBar>
    <MudDrawer @bind-Open="DrawerOpen" Elevation="1" Variant="DrawerVariant.Mini"
               OpenMiniOnHover="true">
        <MudNavMenu>
            <MudNavLink Href="/monitor" Icon="@Icons.Material.Filled.LibraryBooks">Libraries</MudNavLink>
            <MudNavLink Href="/monitor/jobs" Icon="@Icons.Material.Filled.History">Jobs</MudNavLink>
        </MudNavMenu>
    </MudDrawer>
    <MudMainContent>
        <MudContainer MaxWidth="MaxWidth.False" Class="pa-4">
            @Body
        </MudContainer>
    </MudMainContent>
</MudLayout>
```

- [ ] **Step 5: Wire the layout as the default for all routes**

Modify `SaddleRAG.Mcp/Monitor/Routes.razor` so the `<RouteView>` gets `DefaultLayout`:

```razor
@* SaddleRAG.Mcp/Monitor/Routes.razor *@
<Router AppAssembly="@typeof(App).Assembly"
        AdditionalAssemblies="@(new[] { typeof(SaddleRAG.Monitor.Theme.WyomingTheme).Assembly })">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)"/>
    </Found>
    <NotFound>
        <LayoutView Layout="@typeof(MainLayout)">
            <MudText Typo="Typo.h6">Page not found.</MudText>
        </LayoutView>
    </NotFound>
</Router>
```

- [ ] **Step 6: Run the test to verify it passes**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MainLayoutTests"
```
Expected: PASS.

- [ ] **Step 7: Build the full solution**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

`msg.txt`:
```
feat(monitor): add MainLayout with AppBar and Drawer nav

Wraps every Razor page in a MudLayout shell so Libraries / Jobs
navigation is reachable from any page. Uses Wyoming brown primary
already configured by the WyomingTheme MudThemeProvider.
```
```
git add SaddleRAG.Mcp/Monitor/MainLayout.razor SaddleRAG.Mcp/Monitor/MainLayout.razor.cs SaddleRAG.Mcp/Monitor/Routes.razor SaddleRAG.Tests/Monitor/MainLayoutTests.cs
git commit -F msg.txt
```

---

### Task 1.2: Sort the library card grid alphabetically

**Files:**
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs`
- Test: `SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs` (created in Phase 3 if not yet present — for now create the file with this single test)

- [ ] **Step 1: Write the failing test**

If `MonitorDataServiceEnrichmentTests.cs` does not yet exist, create it:

```csharp
// SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorDataServiceEnrichmentTests
{
    [Fact]
    public async Task GetLibrarySummariesAsyncSortsAlphabeticallyCaseInsensitive()
    {
        var libRepo = new FakeLibraryRepository(
            new LibraryRecord { Id = "zeta",  CurrentVersion = "1", Hint = null },
            new LibraryRecord { Id = "Alpha", CurrentVersion = "1", Hint = null },
            new LibraryRecord { Id = "mongodb.driver", CurrentVersion = "1", Hint = null }
        );
        var svc = new MonitorDataService(libRepo);

        var summaries = await svc.GetLibrarySummariesAsync();
        var ids = summaries.Select(s => s.LibraryId).ToList();

        Assert.Equal(new[] { "Alpha", "mongodb.driver", "zeta" }, ids);
    }
}

internal sealed class FakeLibraryRepository : ILibraryRepository
{
    public FakeLibraryRepository(params LibraryRecord[] libs) { mLibs = libs; }
    private readonly LibraryRecord[] mLibs;
    public Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryRecord>>(mLibs);
    public Task<LibraryRecord?> GetLibraryAsync(string id, CancellationToken ct = default)
        => Task.FromResult(mLibs.FirstOrDefault(l => l.Id == id));
    public Task UpsertLibraryAsync(LibraryRecord r, CancellationToken ct = default) => Task.CompletedTask;
    public Task<LibraryVersionRecord?> GetVersionAsync(string lib, string ver, CancellationToken ct = default)
        => Task.FromResult<LibraryVersionRecord?>(null);
    public Task UpsertVersionAsync(LibraryVersionRecord r, CancellationToken ct = default) => Task.CompletedTask;
    public Task<DeleteVersionResult> DeleteVersionAsync(string lib, string ver, CancellationToken ct = default)
        => Task.FromResult(new DeleteVersionResult { LibraryDeleted = false, NewCurrentVersion = null });
    public Task<long> DeleteAsync(string lib, CancellationToken ct = default) => Task.FromResult(0L);
    public Task<RenameLibraryResponse> RenameAsync(string a, string b, CancellationToken ct = default)
        => Task.FromResult(new RenameLibraryResponse());
    public Task SetSuspectAsync(string lib, string ver, IReadOnlyList<string> r, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task ClearSuspectAsync(string lib, string ver, CancellationToken ct = default) => Task.CompletedTask;
}
```

- [ ] **Step 2: Confirm `LibraryRecord` and `DeleteVersionResult` shape**

Read `SaddleRAG.Core/Models/LibraryRecord.cs` and `DeleteVersionResult` to confirm the constructor / property names used in `FakeLibraryRepository` actually compile. Adjust the test fake to match real types if necessary.

- [ ] **Step 3: Run the test to verify it fails**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~GetLibrarySummariesAsyncSortsAlphabeticallyCaseInsensitive"
```
Expected: FAIL — order matches input order, not sorted.

- [ ] **Step 4: Apply the sort**

In `SaddleRAG.Monitor/Services/MonitorDataService.cs`, change `GetLibrarySummariesAsync` so the projected list is sorted:

```csharp
public async Task<IReadOnlyList<LibrarySummaryItem>> GetLibrarySummariesAsync(CancellationToken ct = default)
{
    var libs = await mLibraries.GetAllLibrariesAsync(ct);
    var versionTasks = libs.Select(lib => lib.CurrentVersion is not null
                                              ? mLibraries.GetVersionAsync(lib.Id, lib.CurrentVersion, ct)
                                              : Task.FromResult<LibraryVersionRecord?>(result: null)
                                  );
    var versions = await Task.WhenAll(versionTasks);
    return libs.Zip(versions,
                    (lib, ver) => new LibrarySummaryItem
                                      {
                                          LibraryId = lib.Id,
                                          Version = lib.CurrentVersion ?? string.Empty,
                                          ChunkCount = ver?.ChunkCount ?? 0,
                                          PageCount = ver?.PageCount ?? 0,
                                          IsSuspect = ver?.Suspect ?? false,
                                          SuspectReasons = ver?.SuspectReasons ?? Array.Empty<string>(),
                                          LastScrapedAt = ver?.ScrapedAt,
                                          Hint = lib.Hint
                                      }
                   )
               .OrderBy(s => s.LibraryId, StringComparer.OrdinalIgnoreCase)
               .ToList();
}
```

(Note: `SuspectReasons` and `LastScrapedAt` properties on `LibrarySummaryItem` are added in Phase 2 Task 2.1; if running Phase 1 alone, drop those two lines and re-add when Task 2.1 lands.)

- [ ] **Step 5: Run the test to verify it passes**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~GetLibrarySummariesAsyncSortsAlphabeticallyCaseInsensitive"
```
Expected: PASS.

- [ ] **Step 6: Commit**

`msg.txt`:
```
feat(monitor): sort library cards alphabetically (case-insensitive)
```
```
git add SaddleRAG.Monitor/Services/MonitorDataService.cs SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs
git commit -F msg.txt
```

---

## Phase 2 — Wire what's broken (live data + suspect reasons)

The active-jobs strip on the landing page never populates because the SignalR handler discards its payload. `LibraryVersionRecord.SuspectReasons` is persisted but never plumbed to the UI. These two are fixed before any new UI is added so subsequent phases see live data.

### Task 2.1: Carry `SuspectReasons` and `LastScrapedAt` through view models

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/LibrarySummaryItem.cs`
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailData.cs`
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs`
- Test: extends `SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs`

- [ ] **Step 1: Extend the view-model records**

`LibrarySummaryItem.cs`:

```csharp
// LibrarySummaryItem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Minimal summary shown in the library card grid.
/// </summary>
public sealed record LibrarySummaryItem
{
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required int ChunkCount { get; init; }
    public required int PageCount { get; init; }
    public required bool IsSuspect { get; init; }
    public IReadOnlyList<string> SuspectReasons { get; init; } = Array.Empty<string>();
    public DateTime? LastScrapedAt { get; init; }
    public string? Hint { get; init; }
}
```

`LibraryDetailData.cs`:

```csharp
// LibraryDetailData.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Detailed view model for a single library shown on the library detail page.
/// </summary>
public sealed record LibraryDetailData
{
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required int ChunkCount { get; init; }
    public required int PageCount { get; init; }
    public required bool IsSuspect { get; init; }
    public required string? Hint { get; init; }
    public IReadOnlyList<string> SuspectReasons { get; init; } = Array.Empty<string>();
    public DateTime? LastScrapedAt { get; init; }
    public DateTime? LastSuspectEvaluatedAt { get; init; }
    public double BoundaryIssuePct { get; init; }
    public string? EmbeddingProviderId { get; init; }
    public string? EmbeddingModelName { get; init; }
}
```

- [ ] **Step 2: Test the new fields are populated**

Append to `MonitorDataServiceEnrichmentTests.cs`:

```csharp
[Fact]
public async Task GetLibraryDetailAsyncCarriesSuspectReasonsAndScrapedAt()
{
    var scraped = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
    var libRepo = new FakeLibraryRepository(
        new LibraryRecord { Id = "alpha", CurrentVersion = "1", Hint = "hello" });
    libRepo.AddVersion(new LibraryVersionRecord
    {
        Id = "alpha/1", LibraryId = "alpha", Version = "1",
        ScrapedAt = scraped, PageCount = 10, ChunkCount = 100,
        EmbeddingProviderId = "ollama", EmbeddingModelName = "nomic-embed-text",
        EmbeddingDimensions = 768, BoundaryIssuePct = 2.5,
        Suspect = true, SuspectReasons = new[] { "low confidence", "thin docs" }
    });

    var svc = new MonitorDataService(libRepo);
    var detail = await svc.GetLibraryDetailAsync("alpha");

    Assert.NotNull(detail);
    Assert.True(detail!.IsSuspect);
    Assert.Equal(new[] { "low confidence", "thin docs" }, detail.SuspectReasons);
    Assert.Equal(scraped, detail.LastScrapedAt);
    Assert.Equal(2.5, detail.BoundaryIssuePct);
    Assert.Equal("nomic-embed-text", detail.EmbeddingModelName);
}
```

Update `FakeLibraryRepository` to record versions:

```csharp
private readonly Dictionary<string, LibraryVersionRecord> mVersions = new();
public void AddVersion(LibraryVersionRecord v) { mVersions[$"{v.LibraryId}/{v.Version}"] = v; }
public Task<LibraryVersionRecord?> GetVersionAsync(string lib, string ver, CancellationToken ct = default)
    => Task.FromResult(mVersions.GetValueOrDefault($"{lib}/{ver}"));
```

- [ ] **Step 3: Run the test (expect FAIL — fields not yet plumbed)**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~GetLibraryDetailAsyncCarriesSuspectReasonsAndScrapedAt"
```

- [ ] **Step 4: Plumb the fields in `MonitorDataService.GetLibraryDetailAsync`**

```csharp
public async Task<LibraryDetailData?> GetLibraryDetailAsync(string libraryId,
                                                            CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    LibraryDetailData? result = null;
    var lib = await mLibraries.GetLibraryAsync(libraryId, ct);
    if (lib is not null)
    {
        var version = lib.CurrentVersion ?? string.Empty;
        var verRecord = string.IsNullOrEmpty(version)
                            ? null
                            : await mLibraries.GetVersionAsync(lib.Id, version, ct);
        result = new LibraryDetailData
                     {
                         LibraryId = lib.Id,
                         Version = version,
                         ChunkCount = verRecord?.ChunkCount ?? 0,
                         PageCount = verRecord?.PageCount ?? 0,
                         IsSuspect = verRecord?.Suspect ?? false,
                         Hint = lib.Hint,
                         SuspectReasons = verRecord?.SuspectReasons ?? Array.Empty<string>(),
                         LastScrapedAt = verRecord?.ScrapedAt,
                         LastSuspectEvaluatedAt = verRecord?.LastSuspectEvaluatedAt,
                         BoundaryIssuePct = verRecord?.BoundaryIssuePct ?? 0.0,
                         EmbeddingProviderId = verRecord?.EmbeddingProviderId,
                         EmbeddingModelName = verRecord?.EmbeddingModelName
                     };
    }
    return result;
}
```

Also update `GetLibrarySummariesAsync` to include `SuspectReasons` and `LastScrapedAt` (per Task 1.2 step 4).

- [ ] **Step 5: Run tests, verify pass, build, commit**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~MonitorDataServiceEnrichmentTests"
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

`msg.txt`:
```
feat(monitor): plumb SuspectReasons, ScrapedAt, and version metadata to view models
```
```
git add SaddleRAG.Monitor/Pages/LibrarySummaryItem.cs SaddleRAG.Monitor/Pages/LibraryDetailData.cs SaddleRAG.Monitor/Services/MonitorDataService.cs SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs
git commit -F msg.txt
```

---

### Task 2.2: `SuspectChip` component with reason tooltip

**Files:**
- Create: `SaddleRAG.Monitor/Components/SuspectChip.razor`
- Modify: `SaddleRAG.Monitor/Components/LibraryCard.razor`

- [ ] **Step 1: Create the chip**

```razor
@* SaddleRAG.Monitor/Components/SuspectChip.razor *@

@if (IsSuspect)
{
    <MudTooltip Arrow="true" Placement="Placement.Top">
        <ChildContent>
            <MudChip T="string" Size="@Size" Color="Color.Warning" Icon="@Icons.Material.Filled.Warning">
                suspect
            </MudChip>
        </ChildContent>
        <TooltipContent>
            @if (Reasons.Count == 0)
            {
                <MudText Typo="Typo.caption">Marked suspect (no reasons recorded)</MudText>
            }
            else
            {
                <MudText Typo="Typo.caption" Class="mb-1">Reasons:</MudText>
                <MudList T="string" Dense="true">
                    @foreach (var r in Reasons)
                    {
                        <MudListItem Icon="@Icons.Material.Filled.FiberManualRecord"
                                     IconSize="Size.Small">@r</MudListItem>
                    }
                </MudList>
            }
        </TooltipContent>
    </MudTooltip>
}
else
{
    <MudChip T="string" Size="@Size" Color="Color.Success" Icon="@Icons.Material.Filled.CheckCircle">
        healthy
    </MudChip>
}

@code {

    [Parameter]
    [EditorRequired]
    public bool IsSuspect { get; set; }

    [Parameter]
    public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();

    [Parameter]
    public Size Size { get; set; } = Size.Small;

}
```

- [ ] **Step 2: Use it in `LibraryCard.razor`**

Replace the inline `MudChip` with `<SuspectChip IsSuspect="@Library.IsSuspect" Reasons="@Library.SuspectReasons"/>`. The full file becomes:

```razor
@* SaddleRAG.Monitor/Components/LibraryCard.razor *@

<MudCard @onclick="@Navigate" Style="cursor: pointer">
    <MudCardContent>
        <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
            <MudText Typo="Typo.subtitle1">@Library.LibraryId</MudText>
            <SuspectChip IsSuspect="@Library.IsSuspect" Reasons="@Library.SuspectReasons"/>
        </MudStack>
        <MudText Typo="Typo.caption">v@(Library.Version) · @Library.ChunkCount chunks · @Library.PageCount pages</MudText>
        @if (Library.LastScrapedAt is not null)
        {
            <MudText Typo="Typo.caption">Scraped @Library.LastScrapedAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'")</MudText>
        }
        @if (!string.IsNullOrEmpty(Library.Hint))
        {
            <MudText Typo="Typo.body2" Class="mt-1">@Library.Hint</MudText>
        }
    </MudCardContent>
</MudCard>

@code {

    [Parameter]
    [EditorRequired]
    public LibrarySummaryItem Library { get; set; } = default!;

    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    private void Navigate()
    {
        Nav.NavigateTo($"/monitor/libraries/{Library.LibraryId}");
    }

}
```

- [ ] **Step 3: Build & smoke**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 4: Commit**

`msg.txt`:
```
feat(monitor): SuspectChip surfaces reasons via tooltip on library cards
```
```
git add SaddleRAG.Monitor/Components/SuspectChip.razor SaddleRAG.Monitor/Components/LibraryCard.razor
git commit -F msg.txt
```

---

### Task 2.3: Fix the active-jobs strip binding on the landing page

The hub fires `ActiveJobs` (a `IReadOnlyList<string>` of job IDs) every 750 ms. The current handler discards the payload. We will extend the hub method to fetch each ID's snapshot from `IMonitorBroadcaster` and replace the local list. The bUnit tests live separately.

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/LandingPage.razor.cs`
- Modify: `SaddleRAG.Monitor/Pages/LandingPage.razor` (already correct — no change)
- Test: `SaddleRAG.Tests/Monitor/LandingPageActiveJobsTests.cs`

- [ ] **Step 1: Add a server-side snapshot fetch endpoint for landing**

`MonitorBroadcaster` already exposes `GetJobSnapshot(jobId)` and `GetActiveJobIds()`. The cleanest fix is to inject `IMonitorBroadcaster` directly into `LandingPageBase` (it's a singleton in DI) and resolve snapshots locally — no new endpoint needed.

- [ ] **Step 2: Add the failing test**

```csharp
// SaddleRAG.Tests/Monitor/LandingPageActiveJobsTests.cs
// (standard file header)

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Pages;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class LandingPageActiveJobsTests
{
    [Fact]
    public void RebuildActiveJobsResolvesSnapshotsForEachId()
    {
        var bcast = new FakeBroadcaster();
        bcast.SetSnapshot("job-1", new JobTickSnapshot { Counters = new PipelineCounters { PagesQueued = 10 }, RecentFetches = [], RecentRejects = [], RecentErrors = [] });
        bcast.SetSnapshot("job-2", new JobTickSnapshot { Counters = new PipelineCounters { PagesQueued = 5 },  RecentFetches = [], RecentRejects = [], RecentErrors = [] });
        var page = new LandingPageTestable(bcast);

        page.RebuildFromIds(new[] { "job-1", "job-2", "job-missing" });

        Assert.Equal(2, page.ActiveJobsForTest.Count);
        Assert.Contains(page.ActiveJobsForTest, j => j.JobId == "job-1");
        Assert.Contains(page.ActiveJobsForTest, j => j.JobId == "job-2");
    }
}

internal sealed class FakeBroadcaster : IMonitorBroadcaster
{
    private readonly Dictionary<string, JobTickSnapshot> mSnaps = new();
    public void SetSnapshot(string id, JobTickSnapshot s) => mSnaps[id] = s;
    public JobTickSnapshot? GetJobSnapshot(string jobId) => mSnaps.GetValueOrDefault(jobId);
    public IReadOnlyList<string> GetActiveJobIds() => mSnaps.Keys.ToList();
    public void RecordJobStarted(string j, string l, string v, string r) {}
    public void RecordFetch(string j, string u) {}
    public void RecordReject(string j, string u, string r) {}
    public void RecordError(string j, string m) {}
    public void RecordPageClassified(string j) {}
    public void RecordChunkGenerated(string j) {}
    public void RecordChunkEmbedded(string j) {}
    public void RecordPageCompleted(string j) {}
    public void RecordJobCompleted(string j, int n) {}
    public void RecordJobFailed(string j, string m) {}
    public void RecordJobCancelled(string j) {}
    public void RecordSuspectFlag(string j, string l, string v, IReadOnlyList<string> r) {}
    public void Subscribe(string j, Func<JobTickEvent, Task> h) {}
    public void Unsubscribe(string j, Func<JobTickEvent, Task> h) {}
    public void BroadcastTick(string j) {}
}

internal sealed class LandingPageTestable : LandingPageBase
{
    public LandingPageTestable(IMonitorBroadcaster b) { BroadcasterForTest = b; }
    public IMonitorBroadcaster? BroadcasterForTest
    {
        get => Broadcaster;
        set => Broadcaster = value;
    }
    public IReadOnlyList<JobTickSnapshotWithId> ActiveJobsForTest => ActiveJobSnapshots;
}
```

- [ ] **Step 3: Modify `LandingPageBase` to inject `IMonitorBroadcaster` and rebuild on tick**

```csharp
// LandingPage.razor.cs
// (file header)

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LandingPageBase : ComponentBase, IAsyncDisposable
{
    [Inject]
    protected NavigationManager? Nav { get; set; }

    [Inject]
    protected MonitorWriteService? WriteService { get; set; }

    [Inject]
    protected MonitorDataService? DataService { get; set; }

    [Inject]
    protected IMonitorBroadcaster? Broadcaster { get; set; }

    protected List<JobTickSnapshotWithId> ActiveJobSnapshots { get; } = [];
    protected List<LibrarySummaryItem> Libraries { get; } = [];

    private HubConnection? mHub;

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        ArgumentNullException.ThrowIfNull(DataService);
        ArgumentNullException.ThrowIfNull(Broadcaster);

        var summaries = await DataService.GetLibrarySummariesAsync();
        Libraries.Clear();
        Libraries.AddRange(summaries);

        RebuildFromIds(Broadcaster.GetActiveJobIds());

        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<IReadOnlyList<string>>(ActiveJobsEvent, async ids =>
        {
            RebuildFromIds(ids);
            await InvokeAsync(StateHasChanged);
        });

        await mHub.StartAsync();
        await mHub.InvokeAsync(SubscribeLandingMethod);
    }

    public void RebuildFromIds(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(Broadcaster);
        ActiveJobSnapshots.Clear();
        var snapshots = ids.Select(id => new
                                       {
                                           Id = id,
                                           Snap = Broadcaster.GetJobSnapshot(id)
                                       })
                           .Where(p => p.Snap is not null)
                           .Select(p => new JobTickSnapshotWithId
                                            {
                                                JobId = p.Id,
                                                Counters = p.Snap!.Counters,
                                                CurrentHost = p.Snap.CurrentHost,
                                                RecentFetches = p.Snap.RecentFetches,
                                                RecentRejects = p.Snap.RecentRejects,
                                                RecentErrors = p.Snap.RecentErrors
                                            }
                                  );
        ActiveJobSnapshots.AddRange(snapshots);
    }

    protected async Task CancelJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        await WriteService.CancelJobAsync(jobId);
    }

    private const string HubPath = "/monitor/hub";
    private const string ActiveJobsEvent = "ActiveJobs";
    private const string SubscribeLandingMethod = "SubscribeLanding";
}

public sealed record JobTickSnapshotWithId
{
    public required string JobId { get; init; }
    public required PipelineCounters Counters { get; init; }
    public string? CurrentHost { get; init; }
    public required IReadOnlyList<RecentFetch> RecentFetches { get; init; }
    public required IReadOnlyList<RecentReject> RecentRejects { get; init; }
    public required IReadOnlyList<RecentError> RecentErrors { get; init; }
}
```

(The `JobTickSnapshotWithId` record exists because `JobTickSnapshot` does not carry a `JobId` — the hub keys snapshots by group name. Confirm the actual property names by reading `SaddleRAG.Core/Models/Monitor/JobTickSnapshot.cs` before editing.)

- [ ] **Step 4: Update `LandingPage.razor` to bind `Job` parameter to the new shape**

The `JobCardStrip` parameter type is currently `JobTickSnapshot`; switch it to `JobTickSnapshotWithId` and read `JobId` from there. Update `JobCardStrip.razor` parameter type accordingly:

```razor
@* SaddleRAG.Monitor/Components/JobCardStrip.razor — parameter type only *@
@code {
    [Parameter]
    [EditorRequired]
    public JobTickSnapshotWithId Job { get; set; } = default!;
    ...
}
```

- [ ] **Step 5: Run tests to verify pass**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~LandingPageActiveJobsTests"
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

- [ ] **Step 6: Commit**

`msg.txt`:
```
fix(monitor): bind active-jobs strip to broadcaster snapshots on each tick

LandingPage now resolves each active job id to its JobTickSnapshot via
the injected IMonitorBroadcaster, replacing the prior handler that
discarded the SignalR payload and never populated the strip.
```
```
git add SaddleRAG.Monitor/Pages/LandingPage.razor.cs SaddleRAG.Monitor/Components/JobCardStrip.razor SaddleRAG.Tests/Monitor/LandingPageActiveJobsTests.cs
git commit -F msg.txt
```

---

### Task 2.4: Push discrete lifecycle events from `MonitorBroadcaster` through the hub

The spec defines `JobStartedEvent`, `JobCompletedEvent`, `JobFailedEvent`, `JobCancelledEvent`, `SuspectFlagEvent`. `IMonitorBroadcaster` records them server-side, but they never reach the browser. We push them as additional SignalR client methods so future UI (toasts, banners on JobDetail) can react.

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/IMonitorBroadcaster.cs` (no shape change; add `event` style notifications via `IObservable`-like callback list isn't compatible with the current minimal design — instead the Broadcaster will accept an `IHubContext<MonitorHub>` or a delegate so it can fire-and-forget the SignalR send.)
- Modify: `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`
- Modify: `SaddleRAG.Mcp/Hubs/MonitorTickService.cs` — keep the tick loop, also relay discrete events.
- Modify: `SaddleRAG.Mcp/Program.cs` — wire the `IHubContext<MonitorHub>` into the broadcaster (resolve at runtime through `IServiceProvider` if circularity becomes a problem).

The existing pattern (`MonitorTickService` injecting `IHubContext<MonitorHub>`) is the cleanest extension point: each `Record*` call already has a per-job event surface to subscribe via `Subscribe`/`Unsubscribe` on the broadcaster. Repurpose: instead of plumbing through `MonitorBroadcaster`, raise discrete events as a thin **`IMonitorEvents`** observable interface, then have a new hosted service `MonitorLifecycleRelay` subscribe to it and call `IHubContext<MonitorHub>.Clients.All.SendAsync(...)`.

- [ ] **Step 1: Define `IMonitorEvents`**

`SaddleRAG.Core/Interfaces/IMonitorEvents.cs`:

```csharp
// IMonitorEvents.cs
// (file header)

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

public interface IMonitorEvents
{
    event Action<JobStartedEvent>?   JobStarted;
    event Action<JobCompletedEvent>? JobCompleted;
    event Action<JobFailedEvent>?    JobFailed;
    event Action<JobCancelledEvent>? JobCancelled;
    event Action<SuspectFlagEvent>?  SuspectFlagRaised;
}
```

- [ ] **Step 2: Define the missing event records**

Verify which of these already exist in `SaddleRAG.Core/Models/Monitor/`. As of this plan only `SuspectFlagEvent.cs` exists. Add the four missing records:

`SaddleRAG.Core/Models/Monitor/JobStartedEvent.cs`:

```csharp
// JobStartedEvent.cs
// (file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobStartedEvent
{
    public required string JobId { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required string RootUrl { get; init; }
    public required DateTime StartedUtc { get; init; }
}
```

`JobCompletedEvent.cs`:

```csharp
// JobCompletedEvent.cs
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobCompletedEvent
{
    public required string JobId { get; init; }
    public required int IndexedPageCount { get; init; }
    public required DateTime CompletedUtc { get; init; }
}
```

`JobFailedEvent.cs`:

```csharp
// JobFailedEvent.cs
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobFailedEvent
{
    public required string JobId { get; init; }
    public required string ErrorMessage { get; init; }
    public required DateTime FailedUtc { get; init; }
}
```

`JobCancelledEvent.cs`:

```csharp
// JobCancelledEvent.cs
namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobCancelledEvent
{
    public required string JobId { get; init; }
    public required DateTime CancelledUtc { get; init; }
}
```

- [ ] **Step 3: Make `MonitorBroadcaster` implement `IMonitorEvents`**

In `SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs`, add the events and raise them inside the existing `RecordJobStarted/Completed/Failed/Cancelled/SuspectFlag` methods. Keep raises wrapped in `try/catch` so a misbehaving subscriber doesn't kill the pipeline:

```csharp
public event Action<JobStartedEvent>? JobStarted;
public event Action<JobCompletedEvent>? JobCompleted;
public event Action<JobFailedEvent>? JobFailed;
public event Action<JobCancelledEvent>? JobCancelled;
public event Action<SuspectFlagEvent>? SuspectFlagRaised;

public void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl)
{
    // ... existing in-memory state init ...
    SafeRaise(() => JobStarted?.Invoke(new JobStartedEvent
        {
            JobId = jobId, LibraryId = libraryId, Version = version,
            RootUrl = rootUrl, StartedUtc = DateTime.UtcNow
        }));
}

private static void SafeRaise(Action raise)
{
    try { raise(); }
    catch { /* subscriber failed; do not let it propagate into pipeline */ }
}
```

(Apply the same pattern to the four other Record methods; for `RecordSuspectFlag`, raise `SuspectFlagRaised`.)

- [ ] **Step 4: Register `IMonitorEvents` against the broadcaster instance**

In `SaddleRAG.Mcp/Program.cs`, add (next to the existing `MonitorBroadcaster` registration):

```csharp
builder.Services.AddSingleton<IMonitorEvents>(sp => sp.GetRequiredService<MonitorBroadcaster>());
```

- [ ] **Step 5: Add `MonitorLifecycleRelay` hosted service**

`SaddleRAG.Mcp/Hubs/MonitorLifecycleRelay.cs`:

```csharp
// MonitorLifecycleRelay.cs
// (file header)

#region Usings

using Microsoft.AspNetCore.SignalR;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Mcp.Hubs;

public sealed class MonitorLifecycleRelay : IHostedService
{
    public MonitorLifecycleRelay(IMonitorEvents events, IHubContext<MonitorHub> hub)
    {
        mEvents = events;
        mHub = hub;
    }

    private readonly IMonitorEvents mEvents;
    private readonly IHubContext<MonitorHub> mHub;

    public Task StartAsync(CancellationToken ct)
    {
        mEvents.JobStarted        += OnJobStarted;
        mEvents.JobCompleted      += OnJobCompleted;
        mEvents.JobFailed         += OnJobFailed;
        mEvents.JobCancelled      += OnJobCancelled;
        mEvents.SuspectFlagRaised += OnSuspectFlag;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        mEvents.JobStarted        -= OnJobStarted;
        mEvents.JobCompleted      -= OnJobCompleted;
        mEvents.JobFailed         -= OnJobFailed;
        mEvents.JobCancelled      -= OnJobCancelled;
        mEvents.SuspectFlagRaised -= OnSuspectFlag;
        return Task.CompletedTask;
    }

    private void OnJobStarted(JobStartedEvent e)        => Send("JobStarted", e);
    private void OnJobCompleted(JobCompletedEvent e)    => Send("JobCompleted", e);
    private void OnJobFailed(JobFailedEvent e)          => Send("JobFailed", e);
    private void OnJobCancelled(JobCancelledEvent e)    => Send("JobCancelled", e);
    private void OnSuspectFlag(SuspectFlagEvent e)      => Send("SuspectFlag", e);

    private void Send<T>(string method, T payload)
    {
        // Fire-and-forget so we don't block the pipeline thread that raised the event.
        _ = mHub.Clients.All.SendAsync(method, payload);
    }
}
```

Register it: `builder.Services.AddHostedService<MonitorLifecycleRelay>();` in `Program.cs` near the existing `AddHostedService<MonitorTickService>()`.

- [ ] **Step 6: Build & commit (UI subscription comes in later phases when JobDetail consumes them)**

```
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

`msg.txt`:
```
feat(monitor): relay JobStarted/Completed/Failed/Cancelled/SuspectFlag through SignalR

MonitorBroadcaster now raises .NET events from its lifecycle Record*
methods. A new MonitorLifecycleRelay hosted service subscribes and
forwards each event to all connected SignalR clients via
IHubContext<MonitorHub>. Pages can subscribe to "JobStarted" etc.
```
```
git add SaddleRAG.Core/Interfaces/IMonitorEvents.cs SaddleRAG.Core/Models/Monitor/JobStartedEvent.cs SaddleRAG.Core/Models/Monitor/JobCompletedEvent.cs SaddleRAG.Core/Models/Monitor/JobFailedEvent.cs SaddleRAG.Core/Models/Monitor/JobCancelledEvent.cs SaddleRAG.Ingestion/Diagnostics/MonitorBroadcaster.cs SaddleRAG.Mcp/Hubs/MonitorLifecycleRelay.cs SaddleRAG.Mcp/Program.cs
git commit -F msg.txt
```

---

## Phase 3 — Library Detail enrichment

Library detail currently shows `LibraryId`, `Version`, `Chunks`, `Pages`, suspect bool, hint. Spec requires hostname distribution, language mix, boundary issue %, suspect reasons, profile (symbols + casing + separators + callable shapes + confidence), version list, audit summary, and action buttons.

We split into the data plumbing first (data service additions) and then UI (sub-tasks per tab).

### Task 3.1: Add hostname distribution + language mix to `MonitorDataService`

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/IPageRepository.cs` (or whichever interface owns page records — verify by reading `SaddleRAG.Database/Repositories/`. If a method that returns per-page hosts already exists, reuse it; otherwise, add one.)
- Modify: `SaddleRAG.Database/Repositories/<PageRepository>.cs`
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailData.cs` (add `IReadOnlyList<HostBucket> HostnameDistribution` and `IReadOnlyDictionary<string,int> LanguageMix`)
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs`
- Test: extend `MonitorDataServiceEnrichmentTests.cs`

- [ ] **Step 1: Recon — locate the page record repository**

```
Grep "interface IPageRepository" --type cs
Grep "class PageRepository" --type cs
```

Identify the type that lists page records by `(libraryId, version)`. Note its method names / shape.

- [ ] **Step 2: Add `HostBucket` value record**

`SaddleRAG.Core/Models/HostBucket.cs`:

```csharp
// HostBucket.cs
// (file header)
namespace SaddleRAG.Core.Models;

public sealed record HostBucket(string Host, int PageCount);
```

- [ ] **Step 3: Test — write the failing assertion**

Add to `MonitorDataServiceEnrichmentTests.cs`:

```csharp
[Fact]
public async Task GetLibraryDetailAsyncReturnsHostnameDistribution()
{
    // FakeLibraryRepository as before plus FakePageRepository holding 6 pages:
    //   2 from docs.aerotech.com, 3 from help.aerotech.com, 1 from learn.aerotech.com
    var libRepo = new FakeLibraryRepository(new LibraryRecord { Id = "alpha", CurrentVersion = "1", Hint = null });
    libRepo.AddVersion(new LibraryVersionRecord { /* ... */ });
    var pageRepo = new FakePageRepository(
        ("alpha", "1", "https://docs.aerotech.com/a"),
        ("alpha", "1", "https://docs.aerotech.com/b"),
        ("alpha", "1", "https://help.aerotech.com/x"),
        ("alpha", "1", "https://help.aerotech.com/y"),
        ("alpha", "1", "https://help.aerotech.com/z"),
        ("alpha", "1", "https://learn.aerotech.com/q"));

    var svc = new MonitorDataService(libRepo, pageRepo /* once added */);
    var detail = await svc.GetLibraryDetailAsync("alpha");

    var hosts = detail!.HostnameDistribution;
    Assert.Equal(3, hosts.Count);
    Assert.Equal(("help.aerotech.com", 3), (hosts[0].Host, hosts[0].PageCount));
    Assert.Equal(("docs.aerotech.com", 2), (hosts[1].Host, hosts[1].PageCount));
    Assert.Equal(("learn.aerotech.com", 1), (hosts[2].Host, hosts[2].PageCount));
}
```

- [ ] **Step 4: Add the page query method**

If `IPageRepository` has `GetPageUrlsForVersionAsync(string libraryId, string version, CancellationToken)` use it. Otherwise add it (read-only — single new method on the interface plus a Mongo aggregation in the implementation that projects only `Url`).

- [ ] **Step 5: Inject `IPageRepository` into `MonitorDataService` and compute the distribution**

```csharp
public MonitorDataService(ILibraryRepository libraries, IPageRepository pages)
{
    mLibraries = libraries;
    mPages = pages;
}

private readonly IPageRepository mPages;

private static IReadOnlyList<HostBucket> ComputeHostBuckets(IEnumerable<string> urls)
{
    return urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
               .Where(h => h is not null)
               .GroupBy(h => h!, StringComparer.OrdinalIgnoreCase)
               .Select(g => new HostBucket(g.Key, g.Count()))
               .OrderByDescending(b => b.PageCount)
               .ThenBy(b => b.Host, StringComparer.OrdinalIgnoreCase)
               .ToList();
}
```

Call `ComputeHostBuckets(await mPages.GetPageUrlsForVersionAsync(libraryId, version, ct))` inside `GetLibraryDetailAsync` and assign to the new `HostnameDistribution` property on `LibraryDetailData`.

- [ ] **Step 6: Language mix**

Language mix is per-chunk metadata: `Chunk.Category` or similar. Read `SaddleRAG.Core/Models/Chunk.cs` and `SaddleRAG.Core/Enums/DocCategory.cs` to identify the enum field used to partition chunks. If the chunk category corresponds to language buckets ("CSharp", "Python", "JavaScript", "Markdown", etc.), expose a method on `IChunkRepository` (or whichever owns chunks) like `GetCategoryHistogramAsync(libraryId, version)` returning `IReadOnlyDictionary<string,int>`.

Add to `LibraryDetailData`:
```csharp
public IReadOnlyDictionary<string, int> LanguageMix { get; init; } = new Dictionary<string, int>();
```

Tests mirror the hostname test pattern.

- [ ] **Step 7: Build, test, commit**

`msg.txt`:
```
feat(monitor): library detail data carries hostname distribution and language mix
```

---

### Task 3.2: Library detail Overview tab — render the new fields

**Files:**
- Create: `SaddleRAG.Monitor/Components/HostnameDistribution.razor`
- Create: `SaddleRAG.Monitor/Components/LanguageMix.razor`
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor`

- [ ] **Step 1: `HostnameDistribution.razor`**

```razor
@* SaddleRAG.Monitor/Components/HostnameDistribution.razor *@
<MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
    <MudText Typo="Typo.subtitle2" Class="mb-2">Hostnames</MudText>
    @if (Buckets.Count == 0)
    {
        <MudText Typo="Typo.caption">No pages indexed.</MudText>
    }
    else
    {
        <MudTable Items="@Buckets" Dense="true" Hover="true">
            <HeaderContent>
                <MudTh>Host</MudTh><MudTh Style="width:100px">Pages</MudTh><MudTh>Share</MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd Style="font-family:monospace">@context.Host</MudTd>
                <MudTd>@context.PageCount</MudTd>
                <MudTd><MudProgressLinear Value="@SharePct(context.PageCount)" Color="Color.Primary"/></MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudPaper>

@code {

    [Parameter]
    [EditorRequired]
    public IReadOnlyList<HostBucket> Buckets { get; set; } = Array.Empty<HostBucket>();

    private int Total => Buckets.Sum(b => b.PageCount);

    private double SharePct(int n) => Total == 0 ? 0 : (double) n / Total * 100.0;

}
```

- [ ] **Step 2: `LanguageMix.razor`**

```razor
@* SaddleRAG.Monitor/Components/LanguageMix.razor *@
<MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
    <MudText Typo="Typo.subtitle2" Class="mb-2">Language / Category Mix</MudText>
    @if (Mix.Count == 0)
    {
        <MudText Typo="Typo.caption">No chunks classified yet.</MudText>
    }
    else
    {
        <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
            @foreach (var kv in Mix.OrderByDescending(p => p.Value))
            {
                <MudChip T="string" Size="Size.Small" Color="Color.Default">
                    @kv.Key: @kv.Value
                </MudChip>
            }
        </MudStack>
    }
</MudPaper>

@code {

    [Parameter]
    [EditorRequired]
    public IReadOnlyDictionary<string, int> Mix { get; set; } = new Dictionary<string, int>();

}
```

- [ ] **Step 3: Wire into `LibraryDetailPage.razor` Overview tab**

```razor
<MudTabPanel Text="Overview">
    <MudGrid>
        <MudItem xs="12" md="6">
            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2" Class="mb-2">Counts</MudText>
                <MudText>Chunks: @Detail.ChunkCount</MudText>
                <MudText>Pages: @Detail.PageCount</MudText>
                <MudText>Boundary issues: @Detail.BoundaryIssuePct.ToString("F1")%</MudText>
                @if (!string.IsNullOrEmpty(Detail.EmbeddingModelName))
                {
                    <MudText>Embedding: @Detail.EmbeddingProviderId / @Detail.EmbeddingModelName</MudText>
                }
                @if (Detail.LastScrapedAt is not null)
                {
                    <MudText>Last scraped: @Detail.LastScrapedAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'")</MudText>
                }
                @if (Detail.IsSuspect && Detail.SuspectReasons.Count > 0)
                {
                    <MudAlert Severity="Severity.Warning" Class="mt-2" Dense="true">
                        <MudText Typo="Typo.body2" Class="mb-1">Suspect reasons:</MudText>
                        <MudList T="string" Dense="true">
                            @foreach (var r in Detail.SuspectReasons)
                            {
                                <MudListItem Icon="@Icons.Material.Filled.FiberManualRecord" IconSize="Size.Small">@r</MudListItem>
                            }
                        </MudList>
                        @if (Detail.LastSuspectEvaluatedAt is not null)
                        {
                            <MudText Typo="Typo.caption">Evaluated @Detail.LastSuspectEvaluatedAt.Value.ToString("yyyy-MM-dd HH:mm 'UTC'")</MudText>
                        }
                    </MudAlert>
                }
            </MudPaper>
        </MudItem>
        <MudItem xs="12" md="6">
            <HostnameDistribution Buckets="@Detail.HostnameDistribution"/>
            <LanguageMix Mix="@Detail.LanguageMix"/>
        </MudItem>
    </MudGrid>
</MudTabPanel>
```

- [ ] **Step 4: Build & commit**

`msg.txt`:
```
feat(monitor): rich Overview tab on library detail (hosts, languages, suspect reasons)
```

---

### Task 3.3: Profile tab — render `LibraryProfile` (symbols, casing, separators, callable shapes, confidence)

**Files:**
- Create: `SaddleRAG.Monitor/Components/ProfileView.razor`
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs` (new method `GetLibraryProfileAsync`)
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs` (load + expose Profile)
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor` (new Profile tab)
- Test: `SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs` extends.

- [ ] **Step 1: Add `GetLibraryProfileAsync` to `MonitorDataService`**

`LibraryProfileService` in `SaddleRAG.Ingestion.Recon` already loads/saves profiles. Inject `LibraryProfileService` (or its repository if a thinner dep is preferred) and expose:

```csharp
public Task<LibraryProfile?> GetLibraryProfileAsync(string libraryId, string version, CancellationToken ct = default)
    => mProfileRepo.LoadAsync(libraryId, version, ct);
```

Add a unit test that returns a hand-built `LibraryProfile` from a fake repository and asserts the service returns it verbatim.

- [ ] **Step 2: `ProfileView.razor` component**

```razor
@* SaddleRAG.Monitor/Components/ProfileView.razor *@
@if (Profile is null)
{
    <MudAlert Severity="Severity.Info">
        No recon profile recorded for this version. Profile is generated by <code>recon_library</code>
        before a scrape begins; library versions older than the recon flow will not have one.
    </MudAlert>
}
else
{
    <MudGrid>
        <MudItem xs="12" md="6">
            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2">Languages</MudText>
                <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
                    @foreach (var lang in Profile.Languages)
                    {
                        <MudChip T="string" Size="Size.Small">@lang</MudChip>
                    }
                </MudStack>
            </MudPaper>

            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2">Separators</MudText>
                <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
                    @foreach (var sep in Profile.Separators)
                    {
                        <MudChip T="string" Size="Size.Small" Color="Color.Default">@sep</MudChip>
                    }
                </MudStack>
            </MudPaper>

            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2">Callable Shapes</MudText>
                <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
                    @foreach (var s in Profile.CallableShapes)
                    {
                        <MudChip T="string" Size="Size.Small" Color="Color.Tertiary">@s</MudChip>
                    }
                </MudStack>
            </MudPaper>

            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2">Confidence</MudText>
                <MudProgressLinear Value="@(Profile.Confidence * 100.0)"
                                   Color="@(Profile.Confidence > 0.66 ? Color.Success : Profile.Confidence > 0.33 ? Color.Warning : Color.Error)"
                                   Class="my-1"/>
                <MudText Typo="Typo.caption">@Profile.Confidence.ToString("F2") (source: @Profile.Source)</MudText>
                @if (!string.IsNullOrWhiteSpace(Profile.CanonicalInventoryUrl))
                {
                    <MudText Typo="Typo.body2" Class="mt-2">
                        Canonical inventory:
                        <MudLink Href="@Profile.CanonicalInventoryUrl" Target="_blank">@Profile.CanonicalInventoryUrl</MudLink>
                    </MudText>
                }
            </MudPaper>
        </MudItem>

        <MudItem xs="12" md="6">
            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudText Typo="Typo.subtitle2" Class="mb-1">Casing Conventions</MudText>
                <MudSimpleTable Dense="true" Hover="true">
                    <thead><tr><th>Category</th><th>Convention</th></tr></thead>
                    <tbody>
                        <tr><td>Type</td><td>@Profile.Casing.Type</td></tr>
                        <tr><td>Member</td><td>@Profile.Casing.Member</td></tr>
                        <tr><td>Constant</td><td>@Profile.Casing.Constant</td></tr>
                        <tr><td>Local</td><td>@Profile.Casing.Local</td></tr>
                    </tbody>
                </MudSimpleTable>
            </MudPaper>

            <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                    <MudText Typo="Typo.subtitle2">Likely Symbols</MudText>
                    <MudText Typo="Typo.caption">(@Profile.LikelySymbols.Count)</MudText>
                    <MudSpacer/>
                    <MudTextField T="string" Placeholder="Filter…" @bind-Value="Filter"
                                  Variant="Variant.Outlined" Margin="Margin.Dense" Style="max-width:200px"/>
                </MudStack>
                <MudList T="string" Dense="true" Style="max-height:340px; overflow:auto">
                    @foreach (var sym in FilteredSymbols)
                    {
                        <MudListItem Icon="@Icons.Material.Filled.Code">
                            <span style="font-family:monospace">@sym</span>
                        </MudListItem>
                    }
                </MudList>
            </MudPaper>

            @if (Profile.Stoplist.Count > 0)
            {
                <MudPaper Class="pa-3 mb-2" Elevation="0" Outlined="true">
                    <MudText Typo="Typo.subtitle2">Stoplist (@Profile.Stoplist.Count)</MudText>
                    <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="1">
                        @foreach (var s in Profile.Stoplist)
                        {
                            <MudChip T="string" Size="Size.Small" Color="Color.Error" Variant="Variant.Outlined">@s</MudChip>
                        }
                    </MudStack>
                </MudPaper>
            }
        </MudItem>
    </MudGrid>
}

@code {

    [Parameter]
    public LibraryProfile? Profile { get; set; }

    private string? Filter { get; set; }

    private IEnumerable<string> FilteredSymbols => string.IsNullOrWhiteSpace(Filter)
        ? Profile?.LikelySymbols ?? Enumerable.Empty<string>()
        : (Profile?.LikelySymbols ?? Enumerable.Empty<string>())
              .Where(s => s.Contains(Filter, StringComparison.OrdinalIgnoreCase));

}
```

- [ ] **Step 3: Load profile in `LibraryDetailPageBase` and pass to view**

```csharp
protected LibraryProfile? Profile { get; private set; }

protected override async Task OnParametersSetAsync()
{
    ArgumentNullException.ThrowIfNull(DataService);
    Detail = await DataService.GetLibraryDetailAsync(LibraryId);
    if (Detail is not null)
        Profile = await DataService.GetLibraryProfileAsync(LibraryId, Detail.Version);
}
```

In `LibraryDetailPage.razor`, add:

```razor
<MudTabPanel Text="Profile">
    <ProfileView Profile="@Profile"/>
</MudTabPanel>
```

- [ ] **Step 4: Build & commit**

`msg.txt`:
```
feat(monitor): library detail Profile tab renders LibraryProfile contents
```

---

### Task 3.4: Versions tab — list every indexed version

**Files:**
- Modify: `SaddleRAG.Core/Interfaces/ILibraryRepository.cs` — add `Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId, CancellationToken ct = default);`
- Modify: `SaddleRAG.Database/Repositories/LibraryRepository.cs` — implement (Mongo `Find` over `LibraryVersions` filtered by `LibraryId`, sorted by `ScrapedAt` descending).
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs` — expose `GetVersionsAsync(libraryId)`.
- Create: `SaddleRAG.Monitor/Components/VersionList.razor`
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor.cs`
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor`
- Test: `SaddleRAG.Tests/Monitor/MonitorDataServiceEnrichmentTests.cs` extends.

- [ ] **Step 1: Repository test (xUnit) for `GetVersionsAsync`** — verify ordering by `ScrapedAt` descending.

- [ ] **Step 2: Implement repository method and unit-test against an in-memory Mongo replacement (or fake).**

- [ ] **Step 3: Service method**

```csharp
public Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId, CancellationToken ct = default)
    => mLibraries.GetVersionsAsync(libraryId, ct);
```

- [ ] **Step 4: `VersionList.razor`**

```razor
@* SaddleRAG.Monitor/Components/VersionList.razor *@
<MudTable Items="@Versions" Dense="true" Hover="true">
    <HeaderContent>
        <MudTh>Version</MudTh><MudTh>Scraped</MudTh><MudTh>Pages</MudTh><MudTh>Chunks</MudTh><MudTh>Suspect</MudTh><MudTh></MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>
            @if (context.Version == CurrentVersion)
            {
                <MudChip T="string" Size="Size.Small" Color="Color.Primary">current</MudChip>
            }
            <span style="font-family:monospace">@context.Version</span>
        </MudTd>
        <MudTd>@context.ScrapedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")</MudTd>
        <MudTd>@context.PageCount</MudTd>
        <MudTd>@context.ChunkCount</MudTd>
        <MudTd>
            <SuspectChip IsSuspect="@context.Suspect" Reasons="@context.SuspectReasons"/>
        </MudTd>
        <MudTd>
            <!-- Placeholder for delete-version button — wired in Phase 7 -->
        </MudTd>
    </RowTemplate>
</MudTable>

@code {

    [Parameter]
    [EditorRequired]
    public IReadOnlyList<LibraryVersionRecord> Versions { get; set; } = Array.Empty<LibraryVersionRecord>();

    [Parameter]
    public string? CurrentVersion { get; set; }

}
```

- [ ] **Step 5: Wire into Library Detail**

```razor
<MudTabPanel Text="Versions">
    <VersionList Versions="@Versions" CurrentVersion="@Detail.Version"/>
</MudTabPanel>
```

In `LibraryDetailPageBase`, fetch in `OnParametersSetAsync` and store in `protected IReadOnlyList<LibraryVersionRecord> Versions { get; private set; } = Array.Empty<LibraryVersionRecord>();`.

- [ ] **Step 6: Build & commit**

`msg.txt`:
```
feat(monitor): library detail Versions tab lists every indexed version
```

---

### Task 3.5: Audit summary panel inside Library Detail

The Library Detail Audit tab today is a single button to the inspector. Spec wants kept/dropped totals + top 5 reasons + top 5 hosts inline.

**Files:**
- Create: `SaddleRAG.Monitor/Components/AuditSummaryPanel.razor`
- Modify: `SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs` (if needed — `SummarizeAsync` already exists; we need top-N reasons and top-N hosts. Either extend `AuditSummary` shape or add separate methods.)
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs` (resolve "latest jobId for this library/version" + run summary)
- Modify: `SaddleRAG.Monitor/Pages/LibraryDetailPage.razor`

- [ ] **Step 1: Find the latest jobId for `(libraryId, version)`**

`IScrapeJobRepository.ListRecentAsync` exists. Add a focused `GetLatestForLibraryAsync(libraryId, version)` if not present, returning the most-recent terminal job's record (or any record if none completed yet).

- [ ] **Step 2: Extend `AuditSummary` (or add `IReadOnlyList<(string Reason, int Count)> TopSkipReasons` and `IReadOnlyList<(string Host, int Count)> TopHosts`)**

Read `SaddleRAG.Core/Models/Audit/AuditSummary.cs`. If it's still scalar-only, add aggregation that runs:
- `$group` by `SkipReason` for status=Skipped → sort desc, limit 5
- `$group` by `Host` → sort desc, limit 5

- [ ] **Step 3: `AuditSummaryPanel.razor`**

```razor
@* SaddleRAG.Monitor/Components/AuditSummaryPanel.razor *@
@if (Summary is null)
{
    <MudText Typo="Typo.body2">No audit recorded yet for this library version.</MudText>
}
else
{
    <MudGrid>
        <MudItem xs="12">
            <MudStack Row="true" Wrap="Wrap.Wrap" Spacing="2" Class="mb-3">
                <MudChip T="string">Considered: @Summary.TotalConsidered</MudChip>
                <MudChip T="string" Color="Color.Success">Indexed: @Summary.IndexedCount</MudChip>
                <MudChip T="string" Color="Color.Warning">Skipped: @Summary.SkippedCount</MudChip>
                <MudChip T="string" Color="Color.Error">Failed: @Summary.FailedCount</MudChip>
            </MudStack>
        </MudItem>
        <MudItem xs="12" md="6">
            <MudText Typo="Typo.subtitle2" Class="mb-1">Top skip reasons</MudText>
            <MudTable Items="@Summary.TopSkipReasons" Dense="true">
                <RowTemplate>
                    <MudTd>@context.Reason</MudTd>
                    <MudTd Style="width:80px">@context.Count</MudTd>
                </RowTemplate>
            </MudTable>
        </MudItem>
        <MudItem xs="12" md="6">
            <MudText Typo="Typo.subtitle2" Class="mb-1">Top hosts</MudText>
            <MudTable Items="@Summary.TopHosts" Dense="true">
                <RowTemplate>
                    <MudTd>@context.Host</MudTd>
                    <MudTd Style="width:80px">@context.Count</MudTd>
                </RowTemplate>
            </MudTable>
        </MudItem>
        <MudItem xs="12">
            <MudButton Variant="Variant.Text" Color="Color.Primary"
                       Href="@($"/monitor/audits/{JobId}")"
                       Disabled="@string.IsNullOrEmpty(JobId)">View full audit</MudButton>
        </MudItem>
    </MudGrid>
}

@code {

    [Parameter]
    public AuditSummary? Summary { get; set; }

    [Parameter]
    public string? JobId { get; set; }

}
```

- [ ] **Step 4: Wire into the Audit tab on `LibraryDetailPage.razor`**

```razor
<MudTabPanel Text="Audit">
    <AuditSummaryPanel Summary="@AuditSummary" JobId="@LatestJobId"/>
</MudTabPanel>
```

- [ ] **Step 5: Build & commit**

`msg.txt`:
```
feat(monitor): library detail Audit tab shows top skip-reasons and hosts inline
```

---

## Phase 4 — Job Detail enrichment

### Task 4.1: Job header — library/version, elapsed time, status, Cancel

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor`
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs`
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs` (or new `MonitorJobService` — see Phase 6 — but a focused `GetJobInfoAsync(jobId)` here is fine.)

- [ ] **Step 1: Add `GetJobInfoAsync` returning a small VM**

```csharp
public sealed record JobInfo
{
    public required string JobId { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required string Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

public async Task<JobInfo?> GetJobInfoAsync(string jobId, CancellationToken ct = default)
{
    var rec = await mJobs.GetAsync(jobId, ct);
    return rec is null ? null : new JobInfo
        {
            JobId = rec.Id,
            LibraryId = rec.Job.LibraryId,
            Version = rec.Job.Version,
            Status = rec.Status.ToString(),
            StartedAt = rec.StartedAt,
            CompletedAt = rec.CompletedAt,
            ErrorMessage = rec.ErrorMessage
        };
}
```

(Inject `IScrapeJobRepository mJobs` into `MonitorDataService` constructor; verify `ScrapeJob.LibraryId`/`Version` property names by reading `SaddleRAG.Core/Models/ScrapeJob.cs`.)

- [ ] **Step 2: Render the header in `JobDetailPage.razor`**

```razor
@if (Info is not null)
{
    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="3" Class="mb-3">
        <MudText Typo="Typo.h5">
            <MudLink Href="@($"/monitor/libraries/{Info.LibraryId}")">@Info.LibraryId</MudLink>
            <span style="opacity:.7">v@(Info.Version)</span>
        </MudText>
        <MudChip T="string" Size="Size.Small" Color="@StatusColor(Info.Status)">@Info.Status</MudChip>
        @if (Info.StartedAt is not null)
        {
            <MudText Typo="Typo.body2">Elapsed: @Elapsed</MudText>
        }
        <MudSpacer/>
        @if (IsActive)
        {
            <MudButton Variant="Variant.Filled" Color="Color.Error" OnClick="CancelClicked">Cancel</MudButton>
        }
    </MudStack>
}
```

In `JobDetailPageBase`, add:

```csharp
protected JobInfo? Info { get; private set; }

protected bool IsActive => Info is not null
                       && Info.Status is "Queued" or "Running"
                       && Info.CompletedAt is null;

protected string Elapsed => Info?.StartedAt is null
                              ? "—"
                              : (Info.CompletedAt ?? DateTime.UtcNow).Subtract(Info.StartedAt.Value).ToString(@"hh\:mm\:ss");

protected static Color StatusColor(string status) => status switch
{
    "Running"    => Color.Info,
    "Queued"     => Color.Default,
    "Completed"  => Color.Success,
    "Failed"     => Color.Error,
    "Cancelled"  => Color.Warning,
    var _        => Color.Default
};

protected async Task CancelClicked()
{
    ArgumentNullException.ThrowIfNull(WriteService);
    await WriteService.CancelJobAsync(JobId);
}
```

A 1-second timer (`PeriodicTimer` or `System.Threading.Timer`) on the page should `StateHasChanged` while the job is active so `Elapsed` re-renders without waiting on a tick. Wire in `OnInitializedAsync`, dispose in `DisposeAsync`.

- [ ] **Step 3: Build & commit**

`msg.txt`:
```
feat(monitor): job detail header (library/version, status, elapsed, cancel)
```

---

### Task 4.2: Replace `PipelineStrip` with `RatePipelineStrip` (counts + per-second deltas)

**Files:**
- Create: `SaddleRAG.Monitor/Services/RatesAccumulator.cs`
- Test: `SaddleRAG.Tests/Monitor/RatesAccumulatorTests.cs`
- Create: `SaddleRAG.Monitor/Components/RatePipelineStrip.razor`
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor` (use new strip)
- Delete or repurpose: `SaddleRAG.Monitor/Components/PipelineStrip.razor` (keep as a no-rate fallback if useful elsewhere — otherwise delete)

- [ ] **Step 1: TDD — `RatesAccumulator` test**

```csharp
// SaddleRAG.Tests/Monitor/RatesAccumulatorTests.cs
// (file header)

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class RatesAccumulatorTests
{
    [Fact]
    public void RatesReturnsZeroBeforeSecondSample()
    {
        var acc = new RatesAccumulator();
        var rates = acc.Update(new PipelineCounters { PagesFetched = 10 }, sampleAt: T(0));
        Assert.Equal(0.0, rates.PagesFetchedPerSec, 3);
    }

    [Fact]
    public void RatesComputedFromDeltaAndElapsed()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 10 }, sampleAt: T(0));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 12 }, sampleAt: T(1));
        Assert.Equal(2.0, rates.PagesFetchedPerSec, 3);
    }

    [Fact]
    public void RatesUseTwoMostRecentSamplesOnly()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 0 },  T(0));
        acc.Update(new PipelineCounters { PagesFetched = 100 }, T(10));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 102 }, T(11));
        // 2 pages in 1 second between the two most-recent samples = 2/s, not (102/11).
        Assert.Equal(2.0, rates.PagesFetchedPerSec, 3);
    }

    private static DateTime T(int sec) => new DateTime(2026, 1, 1, 0, 0, sec, DateTimeKind.Utc);
}
```

- [ ] **Step 2: Implement `RatesAccumulator`**

```csharp
// RatesAccumulator.cs
// (file header)

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

public sealed class RatesAccumulator
{
    public PipelineRates Update(PipelineCounters now, DateTime sampleAt)
    {
        ArgumentNullException.ThrowIfNull(now);
        var rates = PipelineRates.Zero;
        if (mLastSample is not null)
        {
            var elapsed = (sampleAt - mLastSample.Value.At).TotalSeconds;
            if (elapsed > 0)
                rates = new PipelineRates
                            {
                                PagesFetchedPerSec    = (now.PagesFetched    - mLastSample.Value.Counters.PagesFetched)    / elapsed,
                                PagesClassifiedPerSec = (now.PagesClassified - mLastSample.Value.Counters.PagesClassified) / elapsed,
                                ChunksGeneratedPerSec = (now.ChunksGenerated - mLastSample.Value.Counters.ChunksGenerated) / elapsed,
                                ChunksEmbeddedPerSec  = (now.ChunksEmbedded  - mLastSample.Value.Counters.ChunksEmbedded)  / elapsed,
                                PagesCompletedPerSec  = (now.PagesCompleted  - mLastSample.Value.Counters.PagesCompleted)  / elapsed
                            };
        }

        mLastSample = (sampleAt, now);
        return rates;
    }

    private (DateTime At, PipelineCounters Counters)? mLastSample;
}

public sealed record PipelineRates
{
    public double PagesFetchedPerSec    { get; init; }
    public double PagesClassifiedPerSec { get; init; }
    public double ChunksGeneratedPerSec { get; init; }
    public double ChunksEmbeddedPerSec  { get; init; }
    public double PagesCompletedPerSec  { get; init; }
    public static PipelineRates Zero { get; } = new();
}
```

- [ ] **Step 3: `RatePipelineStrip.razor`**

```razor
@* SaddleRAG.Monitor/Components/RatePipelineStrip.razor *@
<MudStack Row="true" Spacing="2" Class="my-3" Wrap="Wrap.Wrap">
    @foreach (var stage in Stages)
    {
        <MudPaper Elevation="1" Class="pa-2" Style="min-width: 130px; text-align:center">
            <MudText Typo="Typo.caption">@stage.Label</MudText>
            <MudText Typo="Typo.h6">@stage.Count</MudText>
            <MudText Typo="Typo.caption" Color="@(stage.Rate > 0 ? Color.Success : Color.Default)">
                @stage.Rate.ToString("F1") /s
            </MudText>
        </MudPaper>
    }
</MudStack>

@code {

    [Parameter]
    [EditorRequired]
    public PipelineCounters Counters { get; set; } = default!;

    [Parameter]
    [EditorRequired]
    public PipelineRates Rates { get; set; } = PipelineRates.Zero;

    private IReadOnlyList<(string Label, int Count, double Rate)> Stages =>
        [
            ("Crawl",    Counters.PagesFetched,    Rates.PagesFetchedPerSec),
            ("Classify", Counters.PagesClassified, Rates.PagesClassifiedPerSec),
            ("Chunk",    Counters.ChunksGenerated, Rates.ChunksGeneratedPerSec),
            ("Embed",    Counters.ChunksEmbedded,  Rates.ChunksEmbeddedPerSec),
            ("Index",    Counters.PagesCompleted,  Rates.PagesCompletedPerSec)
        ];

}
```

- [ ] **Step 4: Use it in `JobDetailPage.razor`**

In `JobDetailPageBase`, hold a `RatesAccumulator` instance; on each tick handler invocation, compute `Rates = mAcc.Update(tick.Counters, tick.At)` then `StateHasChanged`. Replace `<PipelineStrip ... />` with `<RatePipelineStrip Counters="@CurrentTick.Counters" Rates="@Rates"/>`.

- [ ] **Step 5: Build & commit**

`msg.txt`:
```
feat(monitor): pipeline strip shows per-second rate deltas using a sliding window
```

---

### Task 4.3: Errors panel as a scrollable list

**Files:**
- Create: `SaddleRAG.Monitor/Components/ErrorList.razor`
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor`
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs` (accumulate errors across ticks rather than only-this-tick)

- [ ] **Step 1: Accumulate errors in the page state**

`JobDetailPageBase`:

```csharp
protected List<RecentError> AllErrors { get; } = [];   // accumulated, capped at 200

private void IngestTick(JobTickEvent tick)
{
    foreach (var err in tick.ErrorsThisTick)
        AllErrors.Add(err);
    while (AllErrors.Count > 200)
        AllErrors.RemoveAt(0);
}
```

Call `IngestTick(tick)` from the SignalR handler before `StateHasChanged`.

- [ ] **Step 2: `ErrorList.razor`**

```razor
@* SaddleRAG.Monitor/Components/ErrorList.razor *@
@if (Errors.Count == 0)
{
    <MudText Typo="Typo.caption">No errors.</MudText>
}
else
{
    <MudPaper Outlined="true" Style="max-height:240px; overflow:auto">
        <MudList T="string" Dense="true">
            @foreach (var e in Errors.Reverse())
            {
                <MudListItem Icon="@Icons.Material.Filled.ErrorOutline" IconColor="Color.Error">
                    <MudStack Row="true" Spacing="2">
                        <span style="font-family:monospace; opacity:.6">@e.At.ToString("HH:mm:ss")</span>
                        <span>@e.Message</span>
                    </MudStack>
                </MudListItem>
            }
        </MudList>
    </MudPaper>
}

@code {

    [Parameter]
    [EditorRequired]
    public IReadOnlyList<RecentError> Errors { get; set; } = Array.Empty<RecentError>();

}
```

- [ ] **Step 3: Replace the inline alert with `<ErrorList Errors="@AllErrors"/>` on `JobDetailPage.razor`**

Verify `RecentError.At` field name by reading `SaddleRAG.Core/Models/Monitor/RecentError.cs`.

- [ ] **Step 4: Build & commit**

`msg.txt`:
```
feat(monitor): job detail errors panel scrolls last 200 errors with timestamps
```

---

### Task 4.4: Render terminal jobs from MongoDB + audit log

If a job is `Completed`, `Failed`, or `Cancelled` when the page mounts, the page should:
- Show a banner indicating the final status (success/error/warning).
- Populate the dual feeds from `IScrapeAuditRepository` (last 50 Fetched URLs and last 50 Skipped URLs ordered by `DiscoveredAt`).
- Hide the elapsed timer (the time between StartedAt and CompletedAt is the elapsed value).

**Files:**
- Modify: `SaddleRAG.Monitor/Services/MonitorDataService.cs` — add `GetTerminalFeedsAsync(jobId, limit=50)` returning `(IReadOnlyList<RecentFetch>, IReadOnlyList<RecentReject>)`.
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor.cs` — branch on `IsActive`.
- Modify: `SaddleRAG.Monitor/Pages/JobDetailPage.razor`.

- [ ] **Step 1: Service method**

```csharp
public async Task<(IReadOnlyList<RecentFetch> Fetches, IReadOnlyList<RecentReject> Rejects)> GetTerminalFeedsAsync(
    string jobId, int limit = 50, CancellationToken ct = default)
{
    var fetches = await mAudit.QueryAsync(jobId, AuditStatus.Fetched, skipReason: null, host: null, urlSubstring: null, limit, ct);
    var rejects = await mAudit.QueryAsync(jobId, AuditStatus.Skipped, skipReason: null, host: null, urlSubstring: null, limit, ct);
    return (
        fetches.Select(e => new RecentFetch { Url = e.Url, At = e.DiscoveredAt }).ToList(),
        rejects.Select(e => new RecentReject { Url = e.Url, Reason = e.SkipReason?.ToString() ?? string.Empty, At = e.DiscoveredAt }).ToList()
    );
}
```

(Inject `IScrapeAuditRepository mAudit` into `MonitorDataService` constructor. Verify `RecentFetch`/`RecentReject` field shapes.)

- [ ] **Step 2: Page branch**

```csharp
protected override async Task OnInitializedAsync()
{
    await LoadInfoAsync();
    if (IsActive)
        await ConnectHubAsync();
    else
        await LoadTerminalFeedsAsync();
}

private async Task LoadTerminalFeedsAsync()
{
    ArgumentNullException.ThrowIfNull(DataService);
    var (fetches, rejects) = await DataService.GetTerminalFeedsAsync(JobId);
    TerminalFetches = fetches;
    TerminalRejects = rejects;
}
```

- [ ] **Step 3: Razor switch**

```razor
@if (Info is null)
{
    <MudProgressCircular Indeterminate="true"/>
}
else
{
    @* … header … *@

    @if (IsActive)
    {
        @* live tick-driven view (existing) *@
    }
    else
    {
        <MudAlert Severity="@TerminalSeverity" Class="mb-2">
            Job @Info.Status. @(Info.ErrorMessage ?? string.Empty)
        </MudAlert>
        <MudGrid>
            <MudItem xs="12" md="6">
                <MudText Typo="Typo.subtitle2" Class="mb-1">Fetched URLs (last @TerminalFetches.Count)</MudText>
                <MudList T="string" Dense="true" Style="max-height:480px; overflow:auto">
                    @foreach (var f in TerminalFetches)
                    {
                        <MudListItem><span style="font-family:monospace; font-size:.75rem">@f.Url</span></MudListItem>
                    }
                </MudList>
            </MudItem>
            <MudItem xs="12" md="6">
                <MudText Typo="Typo.subtitle2" Class="mb-1">Skipped URLs (last @TerminalRejects.Count)</MudText>
                <MudList T="string" Dense="true" Style="max-height:480px; overflow:auto">
                    @foreach (var r in TerminalRejects)
                    {
                        <MudListItem>
                            <MudStack Row="true">
                                <span style="font-family:monospace; font-size:.75rem">@r.Url</span>
                                <MudChip T="string" Size="Size.Small">@r.Reason</MudChip>
                            </MudStack>
                        </MudListItem>
                    }
                </MudList>
            </MudItem>
        </MudGrid>
    }
}
```

`TerminalSeverity` returns `Severity.Success` for Completed, `Severity.Error` for Failed, `Severity.Warning` for Cancelled.

- [ ] **Step 4: Build & commit**

`msg.txt`:
```
feat(monitor): job detail renders terminal jobs from audit log with status banner
```

---

## Phase 5 — Audit Inspector polish

### Task 5.1: Per-skip-reason histogram strip

**Files:**
- Modify: `SaddleRAG.Core/Models/Audit/AuditSummary.cs` — add `IReadOnlyList<(string Reason, int Count)> SkipReasonHistogram` (or a record).
- Modify: `SaddleRAG.Database/Repositories/<ScrapeAuditRepository>.cs` — `SummarizeAsync` populates it via `$group` over `SkipReason` for `Status=Skipped`.
- Modify: `SaddleRAG.Monitor/Pages/AuditInspectorPage.razor`.

- [ ] **Step 1: Test the repository aggregation** — fixture inserts known mix, asserts top reasons and counts.

- [ ] **Step 2: Render histogram on the page**

```razor
@if (Summary?.SkipReasonHistogram?.Count > 0)
{
    <MudPaper Outlined="true" Class="pa-2 mb-3">
        <MudText Typo="Typo.subtitle2" Class="mb-1">Skip reasons</MudText>
        @foreach (var bucket in Summary.SkipReasonHistogram.OrderByDescending(b => b.Count))
        {
            var pct = Summary.SkippedCount == 0 ? 0 : (double)bucket.Count / Summary.SkippedCount * 100.0;
            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2" Class="mb-1">
                <MudText Typo="Typo.body2" Style="min-width:160px">@bucket.Reason</MudText>
                <MudProgressLinear Value="@pct" Color="Color.Warning" Class="flex-1"/>
                <MudText Typo="Typo.caption" Style="min-width:80px; text-align:right">@bucket.Count</MudText>
            </MudStack>
        }
    </MudPaper>
}
```

- [ ] **Step 3: Build & commit**

---

### Task 5.2: Add Parent URL column + expandable row detail

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/AuditInspectorPage.razor`

- [ ] **Step 1: Add column + expand panel**

```razor
<MudTable Items="@Entries" Dense="true" Hover="true" T="ScrapeAuditLogEntry">
    <HeaderContent>
        <MudTh>URL</MudTh><MudTh>Host</MudTh><MudTh>Depth</MudTh>
        <MudTh>Status</MudTh><MudTh>Reason</MudTh><MudTh>Parent</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd Style="font-family:monospace; font-size:.72rem; max-width:420px; overflow:hidden; text-overflow:ellipsis">
            @context.Url
        </MudTd>
        <MudTd>@context.Host</MudTd>
        <MudTd>@context.Depth</MudTd>
        <MudTd>
            <MudChip T="string" Size="Size.Small" Color="@StatusColor(context.Status.ToString())">@context.Status</MudChip>
        </MudTd>
        <MudTd>@context.SkipReason</MudTd>
        <MudTd Style="font-family:monospace; font-size:.7rem; max-width:240px; overflow:hidden; text-overflow:ellipsis">
            @(context.ParentUrl ?? "—")
        </MudTd>
    </RowTemplate>
    <ChildRowContent>
        <MudTr>
            <td colspan="6" style="background:#f7f3ed; padding:1rem">
                <MudGrid>
                    <MudItem xs="12" md="6">
                        <MudText Typo="Typo.body2"><strong>Skip detail:</strong> @(context.SkipDetail ?? "—")</MudText>
                        <MudText Typo="Typo.body2"><strong>Discovered:</strong> @context.DiscoveredAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")</MudText>
                    </MudItem>
                    <MudItem xs="12" md="6">
                        @if (context.PageOutcome is not null)
                        {
                            <MudText Typo="Typo.body2"><strong>Fetch:</strong> @context.PageOutcome.FetchStatus</MudText>
                            <MudText Typo="Typo.body2"><strong>Category:</strong> @context.PageOutcome.Category</MudText>
                            <MudText Typo="Typo.body2"><strong>Chunks:</strong> @context.PageOutcome.ChunkCount</MudText>
                            @if (!string.IsNullOrWhiteSpace(context.PageOutcome.Error))
                            {
                                <MudAlert Severity="Severity.Error" Dense="true">@context.PageOutcome.Error</MudAlert>
                            }
                        }
                    </MudItem>
                </MudGrid>
            </td>
        </MudTr>
    </ChildRowContent>
</MudTable>
```

(MudBlazor's `MudTable` supports `<ChildRowContent>` for expandable rows — the row toggles via the built-in arrow column when `Hierarchy` is configured. Verify the API on MudBlazor 8.x; if the syntax differs, use `MudExpansionPanels` instead, one per row, with header showing the columns and expanded body showing the detail.)

- [ ] **Step 2: Build & commit**

---

## Phase 6 — Job History page (`/monitor/jobs`)

This is the user's "where is the queue of all the tasks it run" — a paged, filterable index of every scrape job ever recorded.

### Task 6.1: `MonitorJobService` — query/list jobs

**Files:**
- Create: `SaddleRAG.Monitor/Services/MonitorJobService.cs`
- Modify: `SaddleRAG.Mcp/Program.cs` (DI registration)
- Test: `SaddleRAG.Tests/Monitor/MonitorJobServiceTests.cs`

- [ ] **Step 1: Service**

```csharp
// MonitorJobService.cs
// (file header)

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Monitor.Services;

public sealed class MonitorJobService
{
    public MonitorJobService(IScrapeJobRepository jobs)
    {
        mJobs = jobs;
    }

    private readonly IScrapeJobRepository mJobs;

    public sealed record JobHistoryRow
    {
        public required string JobId { get; init; }
        public required string LibraryId { get; init; }
        public required string Version { get; init; }
        public required string Status { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int IndexedPageCount { get; init; }
        public int ErrorCount { get; init; }
        public string? ErrorMessage { get; init; }
        public TimeSpan? Duration => StartedAt is null ? null
                                                       : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
    }

    public async Task<IReadOnlyList<JobHistoryRow>> ListAsync(
        ScrapeJobStatus? status = null,
        string? libraryIdFilter = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var raw = await mJobs.ListRecentAsync(limit * 2, ct);
        var filtered = raw.Where(r => status is null || r.Status == status)
                          .Where(r => string.IsNullOrEmpty(libraryIdFilter)
                                   || r.Job.LibraryId.Contains(libraryIdFilter, StringComparison.OrdinalIgnoreCase))
                          .Take(limit)
                          .Select(r => new JobHistoryRow
                                           {
                                               JobId            = r.Id,
                                               LibraryId        = r.Job.LibraryId,
                                               Version          = r.Job.Version,
                                               Status           = r.Status.ToString(),
                                               CreatedAt        = r.CreatedAt,
                                               StartedAt        = r.StartedAt,
                                               CompletedAt      = r.CompletedAt,
                                               IndexedPageCount = r.PagesCompleted,
                                               ErrorCount       = r.ErrorCount,
                                               ErrorMessage     = r.ErrorMessage
                                           });
        return filtered.ToList();
    }
}
```

- [ ] **Step 2: Tests** — mirror the `MonitorDataServiceEnrichmentTests` style with a fake `IScrapeJobRepository`. Cover: status filter, library filter, limit, ordering preserved from `ListRecentAsync`.

- [ ] **Step 3: DI**

In `Program.cs`:
```csharp
builder.Services.AddSingleton<MonitorJobService>();
```

- [ ] **Step 4: Build & commit**

---

### Task 6.2: `JobHistoryPage` (`/monitor/jobs`)

**Files:**
- Create: `SaddleRAG.Monitor/Pages/JobHistoryPage.razor`
- Create: `SaddleRAG.Monitor/Pages/JobHistoryPage.razor.cs`

- [ ] **Step 1: Code-behind**

```csharp
// JobHistoryPage.razor.cs
// (file header)

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Enums;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class JobHistoryPageBase : ComponentBase
{
    [Inject]
    private MonitorJobService? Jobs { get; set; }

    protected IReadOnlyList<MonitorJobService.JobHistoryRow> Rows { get; private set; } = [];
    protected string? StatusFilter { get; set; }
    protected string? LibraryFilter { get; set; }
    protected int LimitChoice { get; set; } = 100;

    protected static readonly string[] pmStatusChoices =
        Enum.GetNames<ScrapeJobStatus>();

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    protected async Task LoadAsync()
    {
        ArgumentNullException.ThrowIfNull(Jobs);
        ScrapeJobStatus? statusEnum = null;
        if (!string.IsNullOrEmpty(StatusFilter)
         && Enum.TryParse<ScrapeJobStatus>(StatusFilter, ignoreCase: true, out var v))
            statusEnum = v;
        Rows = await Jobs.ListAsync(statusEnum, LibraryFilter, LimitChoice);
    }
}
```

- [ ] **Step 2: View**

```razor
@* SaddleRAG.Monitor/Pages/JobHistoryPage.razor *@
@page "/monitor/jobs"
@rendermode InteractiveServer
@inherits JobHistoryPageBase

<MudText Typo="Typo.h4" Class="mb-3">Jobs</MudText>

<MudStack Row="true" Spacing="2" Class="mb-3" Wrap="Wrap.Wrap">
    <MudSelect T="string" Label="Status" @bind-Value="StatusFilter" Clearable="true" Style="min-width:140px">
        @foreach (var s in pmStatusChoices)
        {
            <MudSelectItem Value="@s">@s</MudSelectItem>
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
          OnRowClick="@(args => Navigate(args.Item))">
    <HeaderContent>
        <MudTh>Created</MudTh>
        <MudTh>Library</MudTh>
        <MudTh>Version</MudTh>
        <MudTh>Status</MudTh>
        <MudTh>Duration</MudTh>
        <MudTh>Indexed</MudTh>
        <MudTh>Errors</MudTh>
        <MudTh>Job</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")</MudTd>
        <MudTd>@context.LibraryId</MudTd>
        <MudTd>@context.Version</MudTd>
        <MudTd><MudChip T="string" Size="Size.Small" Color="@StatusColor(context.Status)">@context.Status</MudChip></MudTd>
        <MudTd>@(context.Duration?.ToString(@"hh\:mm\:ss") ?? "—")</MudTd>
        <MudTd>@context.IndexedPageCount</MudTd>
        <MudTd>@context.ErrorCount</MudTd>
        <MudTd Style="font-family:monospace; font-size:.7rem">@context.JobId</MudTd>
    </RowTemplate>
    <NoRecordsContent><MudText>No jobs match the current filters.</MudText></NoRecordsContent>
</MudTable>

@code {
    [Inject]
    private NavigationManager Nav { get; set; } = default!;

    private static MudBlazor.Color StatusColor(string status) => status switch
    {
        "Running"   => MudBlazor.Color.Info,
        "Queued"    => MudBlazor.Color.Default,
        "Completed" => MudBlazor.Color.Success,
        "Failed"    => MudBlazor.Color.Error,
        "Cancelled" => MudBlazor.Color.Warning,
        var _       => MudBlazor.Color.Default
    };

    private void Navigate(MonitorJobService.JobHistoryRow row) => Nav.NavigateTo($"/monitor/jobs/{row.JobId}");
}
```

- [ ] **Step 3: Build & commit**

`msg.txt`:
```
feat(monitor): /monitor/jobs index page lists job history with filters
```

---

## Phase 7 — Library hero action buttons (Rescrape / Rescrub / Delete version)

These are write actions; they must go behind the existing `DiagnosticsWrite` policy.

### Task 7.1: New write endpoints

**Files:**
- Create: `SaddleRAG.Mcp/Api/MonitorLibraryActionsEndpoints.cs`
- Modify: `SaddleRAG.Mcp/Program.cs` (call `MonitorLibraryActionsEndpoints.Map(app)`)
- Modify: `SaddleRAG.Monitor/Services/MonitorWriteService.cs` (add three methods that POST to the new endpoints)
- Test: `SaddleRAG.Tests/Monitor/MonitorLibraryActionsEndpointsTests.cs`

- [ ] **Step 1: Endpoints**

```csharp
// MonitorLibraryActionsEndpoints.cs
// (file header)

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Mcp.Auth;

#endregion

namespace SaddleRAG.Mcp.Api;

public static class MonitorLibraryActionsEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/monitor/libraries")
                       .RequireAuthorization(DiagnosticsWriteRequirement.PolicyName);

        group.MapPost("/{libraryId}/rescrape",
            async (string libraryId, RescrapeRequest req, IScrapeJobQueue queue, ILibraryRepository libs) =>
            {
                var lib = await libs.GetLibraryAsync(libraryId);
                var result = lib is null ? Results.NotFound() : await EnqueueRescrapeAsync(queue, lib, req);
                return result;
            });

        group.MapPost("/{libraryId}/rescrub",
            async (string libraryId, IBackgroundJobRunner runner, ILibraryRepository libs) =>
            {
                var lib = await libs.GetLibraryAsync(libraryId);
                var result = lib is null
                                 ? Results.NotFound()
                                 : await EnqueueRescrubAsync(runner, lib);
                return result;
            });

        group.MapDelete("/{libraryId}/versions/{version}",
            async (string libraryId, string version, ILibraryRepository libs) =>
            {
                var deleted = await libs.DeleteVersionAsync(libraryId, version);
                return Results.Ok(new
                                      {
                                          LibraryDeleted = deleted.LibraryDeleted,
                                          NewCurrentVersion = deleted.NewCurrentVersion
                                      });
            });
    }

    public sealed record RescrapeRequest(string Version);

    private static async Task<IResult> EnqueueRescrapeAsync(IScrapeJobQueue queue, LibraryRecord lib, RescrapeRequest req)
    {
        // Pseudocode — adapt to the real ScrapeJobQueue.EnqueueAsync signature.
        var jobId = await queue.EnqueueRescrapeAsync(lib.Id, req.Version);
        return Results.Ok(new { JobId = jobId });
    }

    private static async Task<IResult> EnqueueRescrubAsync(IBackgroundJobRunner runner, LibraryRecord lib)
    {
        var jobId = await runner.EnqueueRescrubAsync(lib.Id);
        return Results.Ok(new { JobId = jobId });
    }
}
```

(Verify the actual `IScrapeJobQueue` / `IBackgroundJobRunner` method signatures by reading their files; adjust calls accordingly.)

- [ ] **Step 2: Tests** — `WebApplicationFactory<Program>` integration tests asserting:
  - 401 when policy is enabled with token configured and no Authorization header.
  - 200 when token matches.
  - 404 when libraryId unknown.
  - DELETE returns the new current version on success.

- [ ] **Step 3: Wire UI buttons**

In `LibraryDetailPage.razor` hero block:

```razor
<MudStack Row="true" Spacing="1" Class="mb-2">
    <MudButton StartIcon="@Icons.Material.Filled.Refresh" Variant="Variant.Outlined"
               OnClick="@RescrapeAsync">Rescrape</MudButton>
    <MudButton StartIcon="@Icons.Material.Filled.CleaningServices" Variant="Variant.Outlined"
               OnClick="@RescrubAsync">Rescrub</MudButton>
    <MudButton StartIcon="@Icons.Material.Filled.DeleteForever" Variant="Variant.Outlined"
               Color="Color.Error" OnClick="@DeleteVersionAsync">Delete version</MudButton>
</MudStack>
```

The Delete button opens a `MudDialog` confirming "Delete version <X>?" before calling the endpoint.

- [ ] **Step 4: Build & commit**

---

## Phase 8 — Empty state & polish

### Task 8.1: Empty state with docs link

**Files:**
- Modify: `SaddleRAG.Monitor/Pages/LandingPage.razor`

- [ ] **Step 1: Replace the bare empty card**

```razor
@if (Libraries.Count == 0 && ActiveJobSnapshots.Count == 0)
{
    <MudPaper Class="pa-6 text-center" Elevation="0" Outlined="true">
        <MudIcon Icon="@Icons.Material.Filled.Inventory2" Size="Size.Large" Color="Color.Primary"/>
        <MudText Typo="Typo.h6" Class="mt-2">No libraries indexed yet</MudText>
        <MudText Typo="Typo.body2" Class="mb-2">
            Run <code>start_ingest</code> via Claude Code to scrape a library's documentation.
            See the <MudLink Href="https://github.com/JackalopeTechnologies/SaddleRAG#getting-started" Target="_blank">getting-started guide</MudLink>.
        </MudText>
        <MudText Typo="Typo.caption">
            Tip: <code>recon_library</code> first to build a profile, then <code>scrape_docs</code> to ingest.
        </MudText>
    </MudPaper>
}
```

(Confirm the public README URL before committing.)

- [ ] **Step 2: Build & commit**

`msg.txt`:
```
feat(monitor): friendly empty state with docs link and ingest hint
```

---

### Task 8.2: Manual smoke test (no automation — call out to the operator)

- [ ] **Step 1: Start the app**

```
dotnet run --project SaddleRAG.Mcp -- --launch-profile Dev
```

Expected: Server listens on `http://localhost:6100`. `/` redirects to `/monitor`.

- [ ] **Step 2: Verify pages load and shell is consistent**

- `http://localhost:6100/monitor` — landing with AppBar+Drawer, alphabetical libraries, suspect chips.
- Click a library → detail page with Overview + Profile + Audit + Versions tabs.
- `http://localhost:6100/monitor/jobs` — job history page.
- Trigger a scrape via Claude Code (`start_ingest`); the active-jobs strip populates within 750ms; clicking it lands on `/monitor/jobs/{id}` showing live counters and per-second deltas.
- After job completes, return to job detail — banner indicates Completed status; dual feeds populate from audit.
- Open `/monitor/audits/{jobId}` — histogram strip + parent URL column + expandable detail panel.

- [ ] **Step 3: Final commit if anything was tweaked during smoke**

---

## Phase 9 — Query performance metrics (in-memory, since-server-start)

A ring buffer of every recent query (search/retrieval/embedding call) with duration, plus aggregate stats — reset on process restart. No persistence.

### Task 9.1: `IQueryMetrics` recorder + ring buffer

**Files:**
- Create: `SaddleRAG.Core/Interfaces/IQueryMetrics.cs`
- Create: `SaddleRAG.Core/Models/Monitor/QuerySample.cs`
- Create: `SaddleRAG.Core/Models/Monitor/QueryMetricsSnapshot.cs`
- Create: `SaddleRAG.Ingestion/Diagnostics/QueryMetricsRecorder.cs`
- Modify: `SaddleRAG.Mcp/Program.cs` (DI)
- Test: `SaddleRAG.Tests/Monitor/QueryMetricsRecorderTests.cs`

- [ ] **Step 1: Define the contract and value records**

```csharp
// IQueryMetrics.cs
// (file header)

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

public interface IQueryMetrics
{
    void Record(string operation, TimeSpan duration, bool success, int? resultCount = null, string? note = null);
    QueryMetricsSnapshot Snapshot();
    DateTime ProcessStartedUtc { get; }
}
```

```csharp
// QuerySample.cs
// (file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record QuerySample
{
    public required DateTime At { get; init; }
    public required string Operation { get; init; }
    public required double DurationMs { get; init; }
    public required bool Success { get; init; }
    public int? ResultCount { get; init; }
    public string? Note { get; init; }
}
```

```csharp
// QueryMetricsSnapshot.cs
// (file header)
namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryOperationStats
{
    public required string Operation { get; init; }
    public required int Count { get; init; }
    public required int FailureCount { get; init; }
    public required double AvgMs { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
    public required double MaxMs { get; init; }
}

public sealed record QueryMetricsSnapshot
{
    public required DateTime ProcessStartedUtc { get; init; }
    public required IReadOnlyList<QuerySample> RecentSamples { get; init; }
    public required IReadOnlyList<QueryOperationStats> PerOperation { get; init; }
}
```

- [ ] **Step 2: TDD — recorder tests**

```csharp
// SaddleRAG.Tests/Monitor/QueryMetricsRecorderTests.cs
// (file header)

#region Usings

using SaddleRAG.Ingestion.Diagnostics;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class QueryMetricsRecorderTests
{
    [Fact]
    public void RingBufferCapsAtCapacity()
    {
        var rec = new QueryMetricsRecorder(capacity: 3);
        rec.Record("search", TimeSpan.FromMilliseconds(10), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(20), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(30), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(40), success: true);

        var snap = rec.Snapshot();
        Assert.Equal(3, snap.RecentSamples.Count);
        Assert.Equal(20.0, snap.RecentSamples[0].DurationMs, 3);   // oldest of the kept 3
        Assert.Equal(40.0, snap.RecentSamples[2].DurationMs, 3);   // newest
    }

    [Fact]
    public void PerOperationStatsAreGroupedAndPercentilesAreReasonable()
    {
        var rec = new QueryMetricsRecorder(capacity: 1024);
        for (int i = 1; i <= 100; i++)
            rec.Record("search", TimeSpan.FromMilliseconds(i), success: true);
        rec.Record("embed", TimeSpan.FromMilliseconds(200), success: true);

        var snap = rec.Snapshot();
        var search = snap.PerOperation.Single(o => o.Operation == "search");
        Assert.Equal(100, search.Count);
        Assert.Equal(0, search.FailureCount);
        Assert.InRange(search.P50Ms, 49, 52);
        Assert.InRange(search.P95Ms, 94, 96);
        Assert.Equal(100.0, search.MaxMs, 3);
    }

    [Fact]
    public void FailureCountTracksUnsuccessfulSamples()
    {
        var rec = new QueryMetricsRecorder(capacity: 100);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: false);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: false);
        var snap = rec.Snapshot();
        var op = snap.PerOperation.Single();
        Assert.Equal(3, op.Count);
        Assert.Equal(2, op.FailureCount);
    }
}
```

- [ ] **Step 3: Implement `QueryMetricsRecorder`**

```csharp
// QueryMetricsRecorder.cs
// (file header)

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

public sealed class QueryMetricsRecorder : IQueryMetrics
{
    public QueryMetricsRecorder(int capacity = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        mCapacity = capacity;
        ProcessStartedUtc = DateTime.UtcNow;
    }

    private readonly int mCapacity;
    private readonly Lock mLock = new();
    private readonly LinkedList<QuerySample> mSamples = new();

    public DateTime ProcessStartedUtc { get; }

    public void Record(string operation, TimeSpan duration, bool success, int? resultCount = null, string? note = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        var sample = new QuerySample
                         {
                             At = DateTime.UtcNow,
                             Operation = operation,
                             DurationMs = duration.TotalMilliseconds,
                             Success = success,
                             ResultCount = resultCount,
                             Note = note
                         };
        lock (mLock)
        {
            mSamples.AddLast(sample);
            while (mSamples.Count > mCapacity)
                mSamples.RemoveFirst();
        }
    }

    public QueryMetricsSnapshot Snapshot()
    {
        QuerySample[] copy;
        lock (mLock)
            copy = mSamples.ToArray();

        var perOp = copy.GroupBy(s => s.Operation, StringComparer.Ordinal)
                        .Select(g =>
                                {
                                    var sorted = g.Select(s => s.DurationMs).OrderBy(d => d).ToArray();
                                    return new QueryOperationStats
                                               {
                                                   Operation    = g.Key,
                                                   Count        = sorted.Length,
                                                   FailureCount = g.Count(s => !s.Success),
                                                   AvgMs        = sorted.Average(),
                                                   P50Ms        = Percentile(sorted, 0.50),
                                                   P95Ms        = Percentile(sorted, 0.95),
                                                   MaxMs        = sorted[^1]
                                               };
                                })
                        .OrderByDescending(s => s.Count)
                        .ToList();

        return new QueryMetricsSnapshot
                   {
                       ProcessStartedUtc = ProcessStartedUtc,
                       RecentSamples = copy,
                       PerOperation = perOp
                   };
    }

    private static double Percentile(double[] sortedAsc, double p)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sortedAsc.Length, 0);
        var rank = p * (sortedAsc.Length - 1);
        var lo = (int) Math.Floor(rank);
        var hi = (int) Math.Ceiling(rank);
        var fraction = rank - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * fraction;
    }
}
```

(.NET 9+ exposes `System.Threading.Lock`. If the project targets a runtime where `Lock` is unavailable, swap to `private readonly object mLock = new();`.)

- [ ] **Step 4: DI registration**

In `Program.cs`:
```csharp
builder.Services.AddSingleton<QueryMetricsRecorder>();
builder.Services.AddSingleton<IQueryMetrics>(sp => sp.GetRequiredService<QueryMetricsRecorder>());
```

- [ ] **Step 5: Build, run tests, commit**

```
dotnet test SaddleRAG.Tests --filter "FullyQualifiedName~QueryMetricsRecorderTests"
dotnet build SaddleRAG.slnx -p:TreatWarningsAsErrors=true
```

`msg.txt`:
```
feat(monitor): in-memory QueryMetricsRecorder with ring buffer and percentile stats
```

---

### Task 9.2: Instrument the query call sites

Identify which operations the recorder should observe. At minimum:

- `search_docs` MCP tool — the user-facing retrieval path.
- `IEmbeddingProvider.EmbedAsync` — embedding latency dominates retrieval.
- `IVectorSearchProvider.SearchAsync` — pure ANN side.
- `IReRanker.RankAsync` — re-rank wrapper around `ToggleableReRanker`.
- (Optional) `MongoDB` `Find` calls from the major repositories.

The clean way is a small helper to avoid try/finally noise at every site:

**Files:**
- Create: `SaddleRAG.Ingestion/Diagnostics/QueryMetricsExtensions.cs`
- Modify: each call site listed above.
- Test: `SaddleRAG.Tests/Monitor/QueryMetricsExtensionsTests.cs`

- [ ] **Step 1: Helper**

```csharp
// QueryMetricsExtensions.cs
// (file header)

#region Usings

using System.Diagnostics;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

public static class QueryMetricsExtensions
{
    public static async Task<T> TimeAsync<T>(this IQueryMetrics metrics,
                                             string operation,
                                             Func<Task<T>> work,
                                             Func<T, int?>? resultCount = null,
                                             string? note = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(work);
        var sw = Stopwatch.StartNew();
        T result;
        try
        {
            result = await work();
        }
        catch
        {
            sw.Stop();
            metrics.Record(operation, sw.Elapsed, success: false, resultCount: null, note: note);
            throw;
        }
        sw.Stop();
        metrics.Record(operation, sw.Elapsed, success: true, resultCount: resultCount?.Invoke(result), note: note);
        return result;
    }
}
```

- [ ] **Step 2: Wrap `search_docs`**

In the MCP tool that handles search (`SaddleRAG.Mcp/Tools/SearchTools.cs` or similar — confirm by `grep`), inject `IQueryMetrics` and replace the inner work with:

```csharp
return await mMetrics.TimeAsync("search_docs",
                                () => mSearch.SearchAsync(query, libraryId, ct),
                                resultCount: r => r.Hits.Count,
                                note: $"library={libraryId}");
```

- [ ] **Step 3: Wrap embedding and vector search calls** with the same pattern. Use unique operation names: `embed_query`, `vector_search`, `rerank`.

- [ ] **Step 4: Tests**

`QueryMetricsExtensionsTests.cs` checks both success and failure branches record exactly one sample, with the right elapsed shape.

- [ ] **Step 5: Build & commit**

`msg.txt`:
```
feat(monitor): time search/embed/vector/rerank paths through IQueryMetrics
```

---

### Task 9.3: Read endpoint + Performance page (`/monitor/performance`)

**Files:**
- Modify: `SaddleRAG.Mcp/Api/MonitorApiEndpoints.cs` — `GET /api/monitor/query-metrics` returns the `QueryMetricsSnapshot`. No auth (read-only).
- Create: `SaddleRAG.Monitor/Pages/PerformancePage.razor`
- Create: `SaddleRAG.Monitor/Pages/PerformancePage.razor.cs`
- Modify: `SaddleRAG.Mcp/Monitor/MainLayout.razor` — add "Performance" nav link.

- [ ] **Step 1: Endpoint**

```csharp
app.MapGet("/api/monitor/query-metrics",
           (IQueryMetrics metrics) => Results.Ok(metrics.Snapshot()));
```

- [ ] **Step 2: Page**

```csharp
// PerformancePage.razor.cs
// (file header)

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class PerformancePageBase : ComponentBase, IDisposable
{
    [Inject]
    private IQueryMetrics? Metrics { get; set; }

    protected QueryMetricsSnapshot? Snapshot { get; private set; }

    private System.Threading.Timer? mTimer;

    protected override Task OnInitializedAsync()
    {
        Refresh();
        mTimer = new System.Threading.Timer(_ => InvokeAsync(() =>
        {
            Refresh();
            StateHasChanged();
        }), state: null, dueTime: 1000, period: 1000);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        mTimer?.Dispose();
    }

    private void Refresh()
    {
        ArgumentNullException.ThrowIfNull(Metrics);
        Snapshot = Metrics.Snapshot();
    }
}
```

```razor
@* SaddleRAG.Monitor/Pages/PerformancePage.razor *@
@page "/monitor/performance"
@rendermode InteractiveServer
@inherits PerformancePageBase

<MudText Typo="Typo.h4" Class="mb-3">Query performance</MudText>

@if (Snapshot is null)
{
    <MudProgressCircular Indeterminate="true"/>
}
else
{
    <MudText Typo="Typo.caption" Class="mb-3">
        Since process start: @Snapshot.ProcessStartedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'")
        (uptime @((DateTime.UtcNow - Snapshot.ProcessStartedUtc).ToString(@"hh\:mm\:ss")))
    </MudText>

    <MudText Typo="Typo.h6" Class="mb-2">By operation</MudText>
    <MudTable Items="@Snapshot.PerOperation" Dense="true" Hover="true">
        <HeaderContent>
            <MudTh>Operation</MudTh><MudTh>Count</MudTh><MudTh>Failures</MudTh>
            <MudTh>Avg ms</MudTh><MudTh>p50</MudTh><MudTh>p95</MudTh><MudTh>Max</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Operation</MudTd>
            <MudTd>@context.Count</MudTd>
            <MudTd>
                <MudChip T="string" Size="Size.Small" Color="@(context.FailureCount > 0 ? Color.Error : Color.Success)">
                    @context.FailureCount
                </MudChip>
            </MudTd>
            <MudTd>@context.AvgMs.ToString("F1")</MudTd>
            <MudTd>@context.P50Ms.ToString("F1")</MudTd>
            <MudTd>@context.P95Ms.ToString("F1")</MudTd>
            <MudTd>@context.MaxMs.ToString("F1")</MudTd>
        </RowTemplate>
    </MudTable>

    <MudText Typo="Typo.h6" Class="mt-4 mb-2">Recent samples (newest first)</MudText>
    <MudTable Items="@Snapshot.RecentSamples.Reverse()" Dense="true" Hover="true">
        <HeaderContent>
            <MudTh>Time</MudTh><MudTh>Op</MudTh><MudTh>Ms</MudTh><MudTh>Result</MudTh><MudTh>Note</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace; font-size:.7rem">@context.At.ToString("HH:mm:ss.fff")</MudTd>
            <MudTd>@context.Operation</MudTd>
            <MudTd>
                <MudText Typo="Typo.body2"
                         Color="@(context.Success ? Color.Default : Color.Error)">
                    @context.DurationMs.ToString("F1")
                </MudText>
            </MudTd>
            <MudTd>@(context.ResultCount?.ToString() ?? "—")</MudTd>
            <MudTd>@context.Note</MudTd>
        </RowTemplate>
    </MudTable>
}
```

- [ ] **Step 3: Add nav link to `MainLayout.razor`**

```razor
<MudLink Href="/monitor/performance" Color="Color.Inherit" Class="mx-2">Performance</MudLink>
```

and a corresponding `MudNavLink` in the drawer.

- [ ] **Step 4: Build & commit**

`msg.txt`:
```
feat(monitor): /monitor/performance page shows query timings since server start
```

---

### Task 9.4: (Optional) Live tail via SignalR

Polling at 1 Hz from the Performance page is fine for v1; SignalR push only saves a tiny amount of bandwidth. Defer unless polling proves too coarse.

---

## Self-Review

**Spec coverage:**

| Spec section | Plan task |
|---|---|
| Layout/header nav | Task 1.1 |
| Active jobs strip — counters, progress, Cancel | Task 2.3 (binding); Cancel already exists |
| Library card grid: id/version/chunks/pages/health/hint | Task 1.2 (sort) + 2.2 (suspect chip) |
| Empty state with docs link | Task 8.1 |
| Job detail header: library/version/elapsed/status/Cancel | Task 4.1 |
| Pipeline strip with absolute count + per-second delta | Task 4.2 |
| Errors panel: last 20 errors | Task 4.3 |
| Completed/failed/cancelled job rendering from MongoDB + audit | Task 4.4 |
| Library detail hero: id/version/hint/last-scraped/health flag with reasons | Task 2.1 + 3.2 |
| Library detail Overview: chunk/page counts, hostname distribution, language mix, boundary issue %, suspect reasons | Task 3.1 + 3.2 |
| Library detail Profile: languages, casing, separators, callable shapes, likely symbols, confidence | Task 3.3 |
| Library detail Audit summary | Task 3.5 |
| Library detail Versions list | Task 3.4 |
| Library detail Rescrape / Rescrub / Delete-version buttons | Task 7.1 |
| Audit inspector filter chips | Existing — confirmed in audit |
| Audit inspector histogram | Task 5.1 |
| Audit inspector URL list with parent URL + expandable detail | Task 5.2 |
| Hub disconnect → polling fallback | Already implemented (commit 071a616) — confirmed |
| Discrete lifecycle events on the wire | Task 2.4 |
| Job history (NEW vs spec) | Tasks 6.1 + 6.2 |
| Query performance metrics, in-memory since process start (NEW vs spec) | Phase 9 (Tasks 9.1–9.3) |
| Pause feature | **Out of scope — separate plan needed** (orchestrator support absent) |

**Type/method consistency:** `JobTickSnapshotWithId` introduced in Task 2.3 is the type used by `JobCardStrip` and the active-jobs strip going forward. `RatesAccumulator.Update` returns `PipelineRates`. `MonitorJobService.JobHistoryRow` is the only history-row shape. `LibraryDetailData` shape is set in Task 2.1 and extended only by adding fields, never renaming.

**Placeholder scan:** No `TBD`/`fill in details`/`similar to Task N` references. Where a method signature is unverified (e.g., `IScrapeJobQueue.EnqueueRescrapeAsync`), the task includes an explicit "verify by reading the file" step rather than guessing.

---

## Execution choice

Plan complete and saved to `docs/superpowers/plans/2026-05-03-monitor-rich-ui.md`.

Two options:

1. **Subagent-Driven (recommended)** — fresh subagent per task, review between tasks; works well for the long phase ladder.
2. **Inline Execution** — execute in this session with batched checkpoints; faster for early phases but loses context-isolation benefits past Phase 3.

Which approach?
