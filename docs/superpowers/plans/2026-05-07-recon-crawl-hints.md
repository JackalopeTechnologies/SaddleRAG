# Recon Crawl Hints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let recon's calling LLM record crawl-scope hints (excluded URL patterns, expected hosts, notes) alongside the parser characterization, and propagate `ExcludedUrlPatterns` into the `start_ingest` → `scrape_docs` handoff so future scrapes don't have to rediscover auth walls.

**Architecture:** Three layers touched.
1. **Core model:** new `CrawlHints` record stored on `LibraryProfile`. `CurrentSchemaVersion` bumped 2 → 3. Hash deliberately excludes hints (they affect crawl, not classify).
2. **Recon:** `recon_library` instruction text, JSON example, hints array, and `submit_library_profile`'s parser learn about a new top-level `crawlHints` object. Recon instructions also mention `dryrun_scrape` as a recommended pre-submit step on complex sites.
3. **State machine:** `IngestStatusResponse` gains `RecommendedExcludedUrlPatterns`. `start_ingest`'s `READY_TO_SCRAPE` branch reads `LibraryProfile.CrawlHints.ExcludedUrlPatterns` and surfaces them on the response so the next `scrape_docs` call gets them automatically.

`dryrun_scrape` itself does not change in this plan — that's a separate "warnings in dryrun output" effort tracked as a follow-up.

**Tech Stack:** .NET 10, C# (Penske coding standards: m-prefix instance fields, sm-prefix statics, Allman braces, single return, `ArgumentException`/`ArgumentNullException` at method entry, expression-bodied switches where applicable). MongoDB.Driver for persistence (POCO deserializer handles missing fields with defaults — no migration script needed). xunit + NSubstitute for tests. Solution: `SaddleRAG.slnx`.

**Key references the engineer should re-read before starting:**
- This conversation's actipro diagnosis: scrape job `6fb2231e` on `actipro-wpf` 25.1 — 7,279,405 URL considerations, 0 indexed, caused by `/support/account/login?returnUrl=...` auth wall multiplying URL space.
- `CLAUDE.md` (repo root) — no AI attribution in commits/PRs, ever. Commit via `git commit -F <message-file>`.
- `~/.claude/CLAUDE.md` — single-return rule, no continue, no if/else/if chains, named constants, m/sm prefixes.
- `docs/superpowers/plans/2026-04-27-mcp-tool-ux.md` — established plan style and commit-message format in this repo.
- `SaddleRAG.Tests/Mcp/UrlCorrectionToolsTests.cs` — established `RepositoryFactory` mocking pattern.
- `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs` — established profile-test style.

---

## File Structure

### Files created
- `SaddleRAG.Core/Models/CrawlHints.cs` — record holding `ExcludedUrlPatterns`, `ExpectedHosts`, `Notes`.
- `SaddleRAG.Tests/Mcp/ReconToolsTests.cs` — covers `recon_library` payload shape and `submit_library_profile` parsing of `crawlHints`.

### Files modified
- `SaddleRAG.Core/Models/LibraryProfile.cs` — add `CrawlHints CrawlHints { get; init; } = new CrawlHints();`. Bump `CurrentSchemaVersion` 2 → 3.
- `SaddleRAG.Core/Models/IngestStatusResponse.cs` — add `IReadOnlyList<string> RecommendedExcludedUrlPatterns { get; init; } = [];`.
- `SaddleRAG.Ingestion/Recon/LibraryProfileService.cs` — extend `Build` signature with `CrawlHints crawlHints` (positioned before `confidence`); extend `ApplyStoplistCarryForwardAsync` (or add a sibling) to also carry forward CrawlHints when new profile's `ExcludedUrlPatterns` is empty. `ComputeHash` stays unchanged — CrawlHints intentionally excluded.
- `SaddleRAG.Mcp/Tools/ReconTools.cs` — extend `ReconInstructions`, `JsonExample`, `smReconHints`; extend `ParseProfileJson` to read `crawlHints`; pass through to `LibraryProfileService.Build`.
- `SaddleRAG.Mcp/Tools/IngestTools.cs` — `MakeReadyToScrape` signature gains `IReadOnlyList<string> excludedUrlPatterns`. Make it `internal static` for direct unit testing. `ResolveStatus` passes `libraryProfile?.CrawlHints.ExcludedUrlPatterns ?? []` through.
- `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs` — add CrawlHints build/carry-forward/hash tests.
- `SaddleRAG.Tests/Mcp/IngestStatusResponseTests.cs` — add serialization test for `RecommendedExcludedUrlPatterns`.

---

## Task 1: Add `CrawlHints` record and wire it into `LibraryProfile`

**Files:**
- Create: `SaddleRAG.Core/Models/CrawlHints.cs`
- Modify: `SaddleRAG.Core/Models/LibraryProfile.cs`
- Test: `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs` (add test only — `Build` signature change happens in Task 2)

- [ ] **Step 1: Write the failing test**

In `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs`, add this test (place it next to `BuildDefaultsStoplistToEmpty`):

```csharp
[Fact]
public void LibraryProfileDefaultsCrawlHintsToEmpty()
{
    var profile = new LibraryProfile
                      {
                          Id = "x/1",
                          LibraryId = "x",
                          Version = "1"
                      };

    Assert.NotNull(profile.CrawlHints);
    Assert.Empty(profile.CrawlHints.ExcludedUrlPatterns);
    Assert.Empty(profile.CrawlHints.ExpectedHosts);
    Assert.Equal(string.Empty, profile.CrawlHints.Notes);
}

[Fact]
public void CurrentSchemaVersionIsThreeAfterCrawlHintsAddition()
{
    Assert.Equal(expected: 3, LibraryProfile.CurrentSchemaVersion);
}
```

The first test exercises the new field; the second locks the schema bump.

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: `LibraryProfileDefaultsCrawlHintsToEmpty` fails to compile (`CrawlHints` doesn't exist on `LibraryProfile`); `CurrentSchemaVersionIsThreeAfterCrawlHintsAddition` fails the assertion (current value is 2). Update the existing `CurrentSchemaVersionIsTwoAfterStoplistAddition` test to match — it should be deleted in this step since the new test supersedes it. Remove that old test.

- [ ] **Step 3: Create the `CrawlHints` record**

Create `SaddleRAG.Core/Models/CrawlHints.cs`:

```csharp
// CrawlHints.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Crawl-scope hints recorded by recon while browsing the docs site.
///     Distinct from parser config — these influence which URLs the
///     crawler enqueues, not how chunks are classified. Carried forward
///     across versions of the same library when the new profile's
///     ExcludedUrlPatterns is empty (mirrors Stoplist carry-forward).
/// </summary>
public record CrawlHints
{
    /// <summary>
    ///     Regex patterns the crawler should exclude. Typical entries:
    ///     auth walls (e.g. "/account/login"), search/filter combinatorial
    ///     URLs, marketing pages reachable from the docs root.
    /// </summary>
    public IReadOnlyList<string> ExcludedUrlPatterns { get; init; } = [];

    /// <summary>
    ///     Hosts recon expects the crawl to legitimately visit. Empty
    ///     when recon could not narrow it down. Used as a soft hint —
    ///     the crawler still respects allowedUrlPatterns at runtime.
    /// </summary>
    public IReadOnlyList<string> ExpectedHosts { get; init; } = [];

    /// <summary>
    ///     Free-form notes recon wants to leave for future scrape calls
    ///     (for example "API reference is auth-walled; only conceptual
    ///     docs are publicly scrape-able").
    /// </summary>
    public string Notes { get; init; } = string.Empty;
}
```

- [ ] **Step 4: Add the field to `LibraryProfile`**

In `SaddleRAG.Core/Models/LibraryProfile.cs`, immediately after the `Stoplist` property block (around line 76) add:

```csharp
    /// <summary>
    ///     Crawl-scope hints captured by recon — excluded URL patterns,
    ///     expected hosts, free-form notes. Distinct from parser config:
    ///     these influence which URLs scrape_docs enqueues, not how
    ///     chunks are classified, so they are intentionally excluded
    ///     from ComputeHash.
    /// </summary>
    public CrawlHints CrawlHints { get; init; } = new CrawlHints();
```

In the same file, change `CurrentSchemaVersion`:

```csharp
    public const int CurrentSchemaVersion = 3;
```

- [ ] **Step 5: Run the tests to confirm they pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: both new tests pass; the old `CurrentSchemaVersionIsTwoAfterStoplistAddition` was already deleted in Step 2.

- [ ] **Step 6: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build. If `LibraryProfileService.Build` callers anywhere break, that's because they reference the old hash signature — leave the build red and proceed to Task 2.

If the build is red purely on Task 2's territory (LibraryProfileService.Build callers), commit anyway — this is a multi-step refactor.

- [ ] **Step 7: Commit**

Write `e:/tmp/recon-crawl-hints-task1.txt`:

```
Add CrawlHints record to LibraryProfile

Adds a new top-level field on LibraryProfile that holds crawl-scope
hints (excluded URL patterns, expected hosts, free-form notes). These
are distinct from parser config and intentionally excluded from
ComputeHash so changes do not trigger reclassification. Schema version
bumps to 3.

Field defaults to empty CrawlHints so existing v2 profiles in MongoDB
deserialize cleanly with no migration.
```

Commit:

```
git add SaddleRAG.Core/Models/CrawlHints.cs SaddleRAG.Core/Models/LibraryProfile.cs SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs
git commit -F e:/tmp/recon-crawl-hints-task1.txt
```

---

## Task 2: Extend `LibraryProfileService.Build` to accept `CrawlHints`

**Files:**
- Modify: `SaddleRAG.Ingestion/Recon/LibraryProfileService.cs`
- Modify: `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs`
- Modify: callers (`SaddleRAG.Mcp/Tools/ReconTools.cs` `ParseProfileJson` is the only caller — Task 4 finishes it; for now pass `new CrawlHints()`)

- [ ] **Step 1: Write the failing test**

Add to `LibraryProfileServiceTests.cs`:

```csharp
[Fact]
public void BuildPersistsCrawlHints()
{
    var hints = new CrawlHints
                    {
                        ExcludedUrlPatterns = ["/account/login"],
                        ExpectedHosts = ["docs.example.com"],
                        Notes = "API ref auth-walled"
                    };

    var profile = LibraryProfileService.Build("aerotech-aeroscript",
                                              "2025.3",
                                                  ["AeroScript"],
                                              new CasingConventions { Types = "PascalCase" },
                                                  ["."],
                                                  ["Foo()"],
                                                  ["MoveLinear"],
                                              hints,
                                              canonicalInventoryUrl: null,
                                              confidence: 0.85f,
                                              "calling-llm"
                                             );

    Assert.Equal(new[] { "/account/login" }, profile.CrawlHints.ExcludedUrlPatterns);
    Assert.Equal(new[] { "docs.example.com" }, profile.CrawlHints.ExpectedHosts);
    Assert.Equal("API ref auth-walled", profile.CrawlHints.Notes);
}
```

Also update **all existing `Build` calls** in this test file to include a `new CrawlHints()` argument in the new position (immediately before `canonicalInventoryUrl`). Specifically:
- `BuildPopulatesIdAndCreatedUtc` (line 22)
- `BuildDefaultsStoplistToEmpty` (line 47)
- `BuildThrowsOnEmptyLibraryId` (line 72)
- `MakeProfile` helper (line 207)

Each one needs `new CrawlHints()` inserted between the likely-symbols list and `canonicalInventoryUrl: null`.

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: compile failures because `Build` doesn't accept `CrawlHints` yet.

- [ ] **Step 3: Update `Build` signature**

In `SaddleRAG.Ingestion/Recon/LibraryProfileService.cs`, change `Build` (around line 132) to:

```csharp
public static LibraryProfile Build(string libraryId,
                                   string version,
                                   IReadOnlyList<string> languages,
                                   CasingConventions casing,
                                   IReadOnlyList<string> separators,
                                   IReadOnlyList<string> callableShapes,
                                   IReadOnlyList<string> likelySymbols,
                                   CrawlHints crawlHints,
                                   string? canonicalInventoryUrl,
                                   float confidence,
                                   string source)
{
    ArgumentException.ThrowIfNullOrEmpty(libraryId);
    ArgumentException.ThrowIfNullOrEmpty(version);
    ArgumentNullException.ThrowIfNull(languages);
    ArgumentNullException.ThrowIfNull(casing);
    ArgumentNullException.ThrowIfNull(separators);
    ArgumentNullException.ThrowIfNull(callableShapes);
    ArgumentNullException.ThrowIfNull(likelySymbols);
    ArgumentNullException.ThrowIfNull(crawlHints);
    ArgumentException.ThrowIfNullOrEmpty(source);

    var result = new LibraryProfile
                     {
                         Id = LibraryProfileRepository.MakeId(libraryId, version),
                         LibraryId = libraryId,
                         Version = version,
                         Languages = languages,
                         Casing = casing,
                         Separators = separators,
                         CallableShapes = callableShapes,
                         LikelySymbols = likelySymbols,
                         CrawlHints = crawlHints,
                         CanonicalInventoryUrl = canonicalInventoryUrl,
                         Confidence = confidence,
                         Source = source,
                         CreatedUtc = DateTime.UtcNow
                     };

    return result;
}
```

- [ ] **Step 4: Update `ReconTools.ParseProfileJson` to pass a default**

In `SaddleRAG.Mcp/Tools/ReconTools.cs`, in `ParseProfileJson` (around line 113), change the call to `LibraryProfileService.Build` to insert `crawlHints: new CrawlHints()` between `likely` and `inventoryUrl` (Task 4 will replace this with the real parsed value). This keeps the build green between Task 2 and Task 4.

```csharp
var result = LibraryProfileService.Build(libraryId,
                                         version,
                                         languages,
                                         casing,
                                         separators,
                                         callables,
                                         likely,
                                         new CrawlHints(),
                                         inventoryUrl,
                                         confidence,
                                         source
                                        );
```

- [ ] **Step 5: Run the tests to confirm they pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: all profile-service tests pass.

- [ ] **Step 6: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build.

- [ ] **Step 7: Commit**

Write `e:/tmp/recon-crawl-hints-task2.txt`:

```
Extend LibraryProfileService.Build to accept CrawlHints

Build now requires a CrawlHints argument. ReconTools.ParseProfileJson
passes a placeholder empty CrawlHints for now; the real parser lands
in the recon-instructions task. All existing Build call sites in tests
updated.
```

Commit:

```
git add SaddleRAG.Ingestion/Recon/LibraryProfileService.cs SaddleRAG.Mcp/Tools/ReconTools.cs SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs
git commit -F e:/tmp/recon-crawl-hints-task2.txt
```

---

## Task 3: Carry forward `CrawlHints` from prior versions when new profile's hints are empty

**Files:**
- Modify: `SaddleRAG.Ingestion/Recon/LibraryProfileService.cs`
- Modify: `SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `LibraryProfileServiceTests.cs`:

```csharp
[Fact]
public async Task SaveCarriesForwardCrawlHintsFromPriorVersionWhenEmpty()
{
    var repo = Substitute.For<ILibraryProfileRepository>();
    var prior = MakeProfileWithCrawlHints("1.0",
                                          new CrawlHints
                                              {
                                                  ExcludedUrlPatterns = ["/account/login"],
                                                  ExpectedHosts = ["docs.x.com"],
                                                  Notes = "auth wall"
                                              });
    repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { prior });

    var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
    var newProfile = MakeProfileWithCrawlHints("1.1", new CrawlHints());

    var saved = await service.SaveAsync(repo, newProfile, TestContext.Current.CancellationToken);

    Assert.Equal(new[] { "/account/login" }, saved.CrawlHints.ExcludedUrlPatterns);
    Assert.Equal(new[] { "docs.x.com" }, saved.CrawlHints.ExpectedHosts);
    Assert.Equal("auth wall", saved.CrawlHints.Notes);
}

[Fact]
public async Task SaveDoesNotOverrideNonEmptyCrawlHints()
{
    var repo = Substitute.For<ILibraryProfileRepository>();
    var prior = MakeProfileWithCrawlHints("1.0",
                                          new CrawlHints { ExcludedUrlPatterns = ["/old/path"] });
    repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { prior });

    var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);
    var newProfile = MakeProfileWithCrawlHints("1.1",
                                               new CrawlHints { ExcludedUrlPatterns = ["/new/path"] });

    var saved = await service.SaveAsync(repo, newProfile, TestContext.Current.CancellationToken);

    Assert.Equal(new[] { "/new/path" }, saved.CrawlHints.ExcludedUrlPatterns);
}
```

Add this helper next to `MakeProfileWithStoplist` at the bottom of the test class:

```csharp
private static LibraryProfile MakeProfileWithCrawlHints(string version, CrawlHints hints) =>
    new LibraryProfile
        {
            Id = $"aerotech-aeroscript/{version}",
            LibraryId = "aerotech-aeroscript",
            Version = version,
            Source = "test",
            CrawlHints = hints
        };
```

- [ ] **Step 2: Run tests to confirm failure**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: the two new tests fail because no carry-forward logic exists for CrawlHints yet.

- [ ] **Step 3: Add carry-forward logic**

In `SaddleRAG.Ingestion/Recon/LibraryProfileService.cs`, find `SaveAsync` (around line 44). After the existing call to `ApplyStoplistCarryForwardAsync` (around line 54), chain a second carry-forward call. Update SaveAsync:

```csharp
public async Task<LibraryProfile> SaveAsync(ILibraryProfileRepository repository,
                                            LibraryProfile profile,
                                            CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(repository);
    ArgumentNullException.ThrowIfNull(profile);

    Validate(profile);

    var normalized = Normalize(profile);
    var withStoplist = await ApplyStoplistCarryForwardAsync(repository, normalized, ct);
    var withCarryForward = await ApplyCrawlHintsCarryForwardAsync(repository, withStoplist, ct);

    await repository.UpsertAsync(withCarryForward, ct);
    mLogger.LogInformation("Saved library profile for {LibraryId}/{Version} (source={Source}, confidence={Confidence:F2}, stoplist={StoplistCount}, excludedPatterns={ExcludedCount})",
                           withCarryForward.LibraryId,
                           withCarryForward.Version,
                           withCarryForward.Source,
                           withCarryForward.Confidence,
                           withCarryForward.Stoplist.Count,
                           withCarryForward.CrawlHints.ExcludedUrlPatterns.Count
                          );
    return withCarryForward;
}
```

Add the new helper method directly below `ApplyStoplistCarryForwardAsync`:

```csharp
/// <summary>
///     If the incoming profile has empty ExcludedUrlPatterns and a
///     prior profile for the same LibraryId (any other version) has a
///     non-empty CrawlHints, copy that prior CrawlHints forward whole.
///     Avoids forcing the LLM to re-discover auth walls and exclusion
///     patterns on every version bump. Non-empty incoming
///     ExcludedUrlPatterns is treated as the LLM having an opinion and
///     is never overridden.
/// </summary>
private static async Task<LibraryProfile> ApplyCrawlHintsCarryForwardAsync(ILibraryProfileRepository repository,
                                                                           LibraryProfile profile,
                                                                           CancellationToken ct)
{
    var result = profile;
    if (profile.CrawlHints.ExcludedUrlPatterns.Count == 0)
    {
        var all = await repository.ListAllAsync(ct);
        var prior = all.Where(p => string.Equals(p.LibraryId, profile.LibraryId, StringComparison.Ordinal) &&
                                   !string.Equals(p.Version, profile.Version, StringComparison.Ordinal) &&
                                   p.CrawlHints.ExcludedUrlPatterns.Count > 0
                             )
                       .OrderByDescending(p => p.CreatedUtc)
                       .FirstOrDefault();
        if (prior != null)
            result = profile with { CrawlHints = prior.CrawlHints };
    }

    return result;
}
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: all profile-service tests pass.

- [ ] **Step 5: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build.

- [ ] **Step 6: Commit**

Write `e:/tmp/recon-crawl-hints-task3.txt`:

```
Carry forward CrawlHints across library versions

When a new profile is saved with empty ExcludedUrlPatterns and a prior
version of the same library has non-empty CrawlHints, copy the whole
CrawlHints record forward. Mirrors the existing Stoplist carry-forward
behaviour so version bumps do not force the LLM to re-discover auth
walls.
```

Commit:

```
git add SaddleRAG.Ingestion/Recon/LibraryProfileService.cs SaddleRAG.Tests/Recon/LibraryProfileServiceTests.cs
git commit -F e:/tmp/recon-crawl-hints-task3.txt
```

---

## Task 4: Extend `recon_library` instructions, schema, hints, and parser

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/ReconTools.cs`
- Create: `SaddleRAG.Tests/Mcp/ReconToolsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SaddleRAG.Tests/Mcp/ReconToolsTests.cs`:

```csharp
// ReconToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ReconToolsTests
{
    [Fact]
    public void ReconLibraryPayloadMentionsCrawlHints()
    {
        var json = ReconTools.ReconLibrary("https://docs.example.com", "example", "1.0");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var instructions = root.GetProperty("Instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("crawlHints", instructions, StringComparison.OrdinalIgnoreCase);

        var schemaText = root.GetProperty("JsonSchema").GetString();
        Assert.NotNull(schemaText);
        Assert.Contains("crawlHints", schemaText, StringComparison.Ordinal);
        Assert.Contains("excludedUrlPatterns", schemaText, StringComparison.Ordinal);
        Assert.Contains("expectedHosts", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconLibraryPayloadMentionsDryrun()
    {
        var json = ReconTools.ReconLibrary("https://docs.example.com", "example", "1.0");
        using var doc = JsonDocument.Parse(json);

        var instructions = doc.RootElement.GetProperty("Instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("dryrun_scrape", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitLibraryProfileParsesCrawlHints()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryProfile>());
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(repo);
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);

        const string profileJson = """
                                   {
                                     "languages": ["C#"],
                                     "casing": { "types": "PascalCase" },
                                     "separators": ["."],
                                     "callableShapes": ["Foo()"],
                                     "likelySymbols": ["Bar"],
                                     "crawlHints": {
                                       "excludedUrlPatterns": ["/account/login", "/account/register"],
                                       "expectedHosts": ["docs.example.com"],
                                       "notes": "API ref auth-walled"
                                     },
                                     "confidence": 0.9,
                                     "source": "calling-llm"
                                   }
                                   """;

        var resultJson = await ReconTools.SubmitLibraryProfile(service,
                                                               factory,
                                                               "example",
                                                               "1.0",
                                                               profileJson,
                                                               profile: null,
                                                               TestContext.Current.CancellationToken
                                                              );

        await repo.Received(requiredNumberOfCalls: 1)
                  .UpsertAsync(Arg.Is<LibraryProfile>(p =>
                                                         p.CrawlHints.ExcludedUrlPatterns.Count == 2 &&
                                                         p.CrawlHints.ExcludedUrlPatterns[0] == "/account/login" &&
                                                         p.CrawlHints.ExpectedHosts.Count == 1 &&
                                                         p.CrawlHints.Notes == "API ref auth-walled"
                                                    ),
                               Arg.Any<CancellationToken>()
                              );
        Assert.Contains("\"LibraryId\":", resultJson);
    }

    [Fact]
    public async Task SubmitLibraryProfileTreatsMissingCrawlHintsAsEmpty()
    {
        var repo = Substitute.For<ILibraryProfileRepository>();
        repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryProfile>());
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(repo);
        var service = new LibraryProfileService(NullLogger<LibraryProfileService>.Instance);

        const string profileJson = """
                                   {
                                     "languages": ["C#"],
                                     "casing": { "types": "PascalCase" },
                                     "separators": ["."],
                                     "callableShapes": ["Foo()"],
                                     "likelySymbols": ["Bar"],
                                     "confidence": 0.9,
                                     "source": "calling-llm"
                                   }
                                   """;

        await ReconTools.SubmitLibraryProfile(service,
                                              factory,
                                              "example",
                                              "1.0",
                                              profileJson,
                                              profile: null,
                                              TestContext.Current.CancellationToken
                                             );

        await repo.Received(requiredNumberOfCalls: 1)
                  .UpsertAsync(Arg.Is<LibraryProfile>(p =>
                                                         p.CrawlHints.ExcludedUrlPatterns.Count == 0 &&
                                                         p.CrawlHints.ExpectedHosts.Count == 0 &&
                                                         p.CrawlHints.Notes == string.Empty
                                                    ),
                               Arg.Any<CancellationToken>()
                              );
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~ReconToolsTests" --configuration Release`

Expected: all four fail — the instructions don't yet mention `crawlHints` or `dryrun_scrape`, and the parser ignores `crawlHints`.

- [ ] **Step 3: Update `ReconTools` instructions, schema, hints**

In `SaddleRAG.Mcp/Tools/ReconTools.cs`, replace the `ReconInstructions` constant (around line 212) with:

```csharp
    private const string ReconInstructions = """
                                             Browse the URL and (when useful) one or two sample pages to characterize this docs site.

                                             PARSER CONFIG. Identify the documentation language(s), the casing conventions used for
                                             types / methods / constants / members / parameters, the token separators that appear in
                                             qualified names (".", "::", "->", ":"), recognized callable shapes ("Foo()", "Foo<T>()"),
                                             and 5-30 plausible top-level type / function / parameter names. The likelySymbols list
                                             is a soft hint — if you miss real symbols, the corpus-context rules will recover them,
                                             so optimize for precision (avoid junk like "Each" or "When" picked from prose) over recall.

                                             CRAWL HINTS. While you are browsing, also note any URL patterns the crawler should skip
                                             — auth walls (login redirects, /account/* paths), search/filter combinatorial URLs,
                                             marketing pages reachable from the docs root. Record them in crawlHints.excludedUrlPatterns
                                             as regex-friendly substrings (e.g. "/account/login"). If you can identify the host(s)
                                             that legitimately serve docs (often distinct from the marketing site), list them in
                                             expectedHosts. Use crawlHints.notes for anything else worth carrying forward.

                                             OPTIONAL DRYRUN. If the site looks complex (multiple subdomains, auth walls visible from
                                             sample pages, deep navigation, very large link counts per page), call dryrun_scrape first
                                             to preview crawl scope before submitting the profile. Sites that look simple do not need
                                             this step.

                                             Self-rate your confidence in [0,1]. Return the JSON object as input to submit_library_profile.
                                             """;
```

Replace `JsonExample` (around line 223) with:

```csharp
    private const string JsonExample = """
                                       {
                                         "languages": ["..."],
                                         "casing": {
                                           "types": "PascalCase|camelCase|snake_case|...",
                                           "methods": "...",
                                           "constants": "...",
                                           "members": "...",
                                           "parameters": "..."
                                         },
                                         "separators": [".", "::"],
                                         "callableShapes": ["Foo()", "Foo<T>()"],
                                         "likelySymbols": ["..."],
                                         "crawlHints": {
                                           "excludedUrlPatterns": ["/account/login"],
                                           "expectedHosts": ["docs.example.com"],
                                           "notes": ""
                                         },
                                         "canonicalInventoryUrl": null,
                                         "confidence": 0.0,
                                         "source": "calling-llm"
                                       }
                                       """;
```

Extend `smReconHints` (around line 244) by adding two entries:

```csharp
    private static readonly string[] smReconHints =
        {
            "Look for an enum index page (e.g. *-Enums.htm) — set canonicalInventoryUrl if found.",
            "If the docs cover multiple languages, list them all in languages[] in order of prominence.",
            "Aerotech-style docs use \".\" separators in qualified names like AxisFault.Disabled.",
            "Python docs are PascalCase types and snake_case functions — note that mismatch.",
            "Skip prose words (Each, When, Represents, Values, For, Use) — they are not symbols.",
            "Auth walls (login redirects, /account/* paths) and versioned URLs (?v=24.1) are crawl-scope concerns — note them in crawlHints.excludedUrlPatterns, do not try to capture them as docs.",
            "Use dryrun_scrape if the site looks complex; the report's PagesRemainingInQueue is a leading indicator of scope problems."
        };
```

- [ ] **Step 4: Update `ParseProfileJson` to read `crawlHints`**

In the same file, add a new constant alongside the existing `KeyCanonicalInventoryUrl`:

```csharp
    private const string KeyCrawlHints = "crawlHints";
    private const string KeyExcludedUrlPatterns = "excludedUrlPatterns";
    private const string KeyExpectedHosts = "expectedHosts";
    private const string KeyNotes = "notes";
```

Add a parser helper (place it next to `ReadCasing`):

```csharp
    private static CrawlHints ReadCrawlHints(JsonElement root)
    {
        var result = new CrawlHints();
        if (root.TryGetProperty(KeyCrawlHints, out var hintsProp) && hintsProp.ValueKind == JsonValueKind.Object)
        {
            result = new CrawlHints
                         {
                             ExcludedUrlPatterns = ReadStringArray(hintsProp, KeyExcludedUrlPatterns),
                             ExpectedHosts = ReadStringArray(hintsProp, KeyExpectedHosts),
                             Notes = ReadOptionalString(hintsProp, KeyNotes) ?? string.Empty
                         };
        }

        return result;
    }
```

In `ParseProfileJson` (around line 113), add a call to read crawl hints and pass them to `Build`:

```csharp
private static LibraryProfile ParseProfileJson(string profileJson, string libraryId, string version)
{
    using var doc = JsonDocument.Parse(profileJson);
    var root = doc.RootElement;

    var languages = ReadStringArray(root, KeyLanguages);
    var casing = ReadCasing(root);
    var separators = ReadStringArray(root, KeySeparators);
    var callables = ReadStringArray(root, KeyCallableShapes);
    var likely = ReadStringArray(root, KeyLikelySymbols);
    var crawlHints = ReadCrawlHints(root);
    var inventoryUrl = ReadOptionalString(root, KeyCanonicalInventoryUrl);
    var confidence = ReadConfidence(root);
    var source = ReadOptionalString(root, KeySource) ?? SourceCallingLlm;

    var result = LibraryProfileService.Build(libraryId,
                                             version,
                                             languages,
                                             casing,
                                             separators,
                                             callables,
                                             likely,
                                             crawlHints,
                                             inventoryUrl,
                                             confidence,
                                             source
                                            );
    return result;
}
```

(This replaces the `new CrawlHints()` placeholder added in Task 2 Step 4.)

- [ ] **Step 5: Run the tests to confirm they pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~ReconToolsTests" --configuration Release`

Expected: all four ReconTools tests pass.

- [ ] **Step 6: Run all tests in the touched projects**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~Recon|FullyQualifiedName~ReconToolsTests" --configuration Release`

Expected: existing profile-service tests still green; new recon-tools tests green.

- [ ] **Step 7: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build.

- [ ] **Step 8: Commit**

Write `e:/tmp/recon-crawl-hints-task4.txt`:

```
Teach recon_library about crawl hints and dryrun_scrape

ReconInstructions, JsonExample, and smReconHints now describe a
crawlHints section on the profile JSON (excludedUrlPatterns,
expectedHosts, notes) and mention dryrun_scrape as a recommended
pre-submit step on complex sites. submit_library_profile parses the
new section into LibraryProfile.CrawlHints.
```

Commit:

```
git add SaddleRAG.Mcp/Tools/ReconTools.cs SaddleRAG.Tests/Mcp/ReconToolsTests.cs
git commit -F e:/tmp/recon-crawl-hints-task4.txt
```

---

## Task 5: Surface `RecommendedExcludedUrlPatterns` on `IngestStatusResponse`

**Files:**
- Modify: `SaddleRAG.Core/Models/IngestStatusResponse.cs`
- Modify: `SaddleRAG.Tests/Mcp/IngestStatusResponseTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `SaddleRAG.Tests/Mcp/IngestStatusResponseTests.cs`:

```csharp
[Fact]
public void RecommendedExcludedUrlPatternsDefaultsToEmpty()
{
    var response = new IngestStatusResponse
                       {
                           Status = IngestStatus.ReadyToScrape,
                           LibraryId = "foo",
                           Version = "1.0",
                           Url = "https://example.com"
                       };

    Assert.NotNull(response.RecommendedExcludedUrlPatterns);
    Assert.Empty(response.RecommendedExcludedUrlPatterns);
}

[Fact]
public void RecommendedExcludedUrlPatternsSerializesToJsonArray()
{
    var response = new IngestStatusResponse
                       {
                           Status = IngestStatus.ReadyToScrape,
                           LibraryId = "foo",
                           Version = "1.0",
                           Url = "https://example.com",
                           RecommendedExcludedUrlPatterns = ["/account/login", "/account/register"]
                       };

    var json = JsonSerializer.Serialize(response);

    Assert.Contains("\"RecommendedExcludedUrlPatterns\":[\"/account/login\",\"/account/register\"]",
                    json);
}
```

- [ ] **Step 2: Run tests to confirm failure**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~IngestStatusResponseTests" --configuration Release`

Expected: compile failure — property doesn't exist.

- [ ] **Step 3: Add the property**

In `SaddleRAG.Core/Models/IngestStatusResponse.cs`, add this property at the end of the record (after `NextToolArgs`):

```csharp
    /// <summary>
    ///     URL patterns the crawler should exclude on the next scrape,
    ///     sourced from LibraryProfile.CrawlHints.ExcludedUrlPatterns.
    ///     Empty when no profile is cached or the profile recorded no
    ///     hints. The calling LLM should pass these to scrape_docs as
    ///     excludedUrlPatterns.
    /// </summary>
    public IReadOnlyList<string> RecommendedExcludedUrlPatterns { get; init; } = [];
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~IngestStatusResponseTests" --configuration Release`

Expected: all four tests in this file pass (the two existing plus the two new).

- [ ] **Step 5: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build.

- [ ] **Step 6: Commit**

Write `e:/tmp/recon-crawl-hints-task5.txt`:

```
Add RecommendedExcludedUrlPatterns to IngestStatusResponse

New optional list field on the start_ingest response. Defaults to
empty. Populated by start_ingest in the next task to forward
LibraryProfile.CrawlHints.ExcludedUrlPatterns into scrape_docs args.
```

Commit:

```
git add SaddleRAG.Core/Models/IngestStatusResponse.cs SaddleRAG.Tests/Mcp/IngestStatusResponseTests.cs
git commit -F e:/tmp/recon-crawl-hints-task5.txt
```

---

## Task 6: Wire `start_ingest` to forward `CrawlHints.ExcludedUrlPatterns`

**Files:**
- Modify: `SaddleRAG.Mcp/Tools/IngestTools.cs`
- Create: `SaddleRAG.Tests/Mcp/IngestToolsTests.cs`

The cleanest approach: change `MakeReadyToScrape` to take `IReadOnlyList<string> excludedUrlPatterns`, make it `internal static`, and unit-test it directly. `ResolveStatus` passes `libraryProfile?.CrawlHints.ExcludedUrlPatterns ?? []` through.

- [ ] **Step 1: Write the failing tests**

Create `SaddleRAG.Tests/Mcp/IngestToolsTests.cs`:

```csharp
// IngestToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class IngestToolsTests
{
    [Fact]
    public void MakeReadyToScrapeIncludesExcludedPatternsWhenSupplied()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login", "/account/register"]);

        Assert.Equal(IngestStatus.ReadyToScrape, response.Status);
        Assert.Equal(new[] { "/account/login", "/account/register" }, response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapeHandlesEmptyPatterns()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                     []);

        Assert.Empty(response.RecommendedExcludedUrlPatterns);
    }

    [Fact]
    public void MakeReadyToScrapePopulatesNextToolArgsForScrapeDocs()
    {
        var response = IngestTools.MakeReadyToScrape("foo",
                                                     "1.0",
                                                     "https://docs.example.com",
                                                     "ready",
                                                         ["/account/login"]);

        Assert.Equal("scrape_docs", response.NextTool);
        Assert.Equal("https://docs.example.com", response.NextToolArgs["url"]);
        Assert.Equal("foo", response.NextToolArgs["libraryId"]);
        Assert.Equal("1.0", response.NextToolArgs["version"]);
    }
}
```

- [ ] **Step 2: Run tests to confirm failure**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~IngestToolsTests" --configuration Release`

Expected: compile failure — `MakeReadyToScrape` is private and signature mismatches.

- [ ] **Step 3: Update `MakeReadyToScrape` signature and visibility**

In `SaddleRAG.Mcp/Tools/IngestTools.cs`, change `MakeReadyToScrape` (around line 172) from `private static` to `internal static` and add the `excludedUrlPatterns` parameter:

```csharp
    internal static IngestStatusResponse MakeReadyToScrape(string library,
                                                           string version,
                                                           string url,
                                                           string message,
                                                           IReadOnlyList<string> excludedUrlPatterns) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.ReadyToScrape,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "scrape_docs",
                Message = message,
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["url"] = url,
                                       ["libraryId"] = library,
                                       ["version"] = version
                                   },
                RecommendedExcludedUrlPatterns = excludedUrlPatterns
            };
```

In the same file, update `ResolveStatus` (around line 114) to pass the patterns through:

```csharp
    private static IngestStatusResponse ResolveStatus(LibraryProfile? libraryProfile,
                                                      int chunkCount,
                                                      bool stale,
                                                      string library,
                                                      string version,
                                                      string url,
                                                      bool force)
    {
        bool hasProfile = libraryProfile != null;
        bool hasChunks = chunkCount > 0;
        IReadOnlyList<string> excludedPatterns = libraryProfile?.CrawlHints.ExcludedUrlPatterns ?? [];

        var response = (hasProfile, hasChunks, stale, force) switch
            {
                (false, var _, var _, var _) => MakeReconNeeded(library, version, url),
                (true, false, var _, var _) =>
                    MakeReadyToScrape(library, version, url, MessageReadyToScrapeFresh, excludedPatterns),
                (true, true, true, var _) => MakeStale(library, version, url),
                (true, true, false, true) =>
                    MakeReadyToScrape(library, version, url, MessageReadyToScrapeForce, excludedPatterns),
                (true, true, false, false) => MakeReady(library, version, url)
            };

        return response;
    }
```

- [ ] **Step 4: Update messages to reference recommended patterns**

In the same file, update the two existing message constants (around line 252) so the LLM knows to read the new field:

```csharp
    private const string MessageReadyToScrapeFresh =
        "Profile cached, no chunks indexed. Call scrape_docs (args in NextToolArgs) to begin ingestion. " +
        "If RecommendedExcludedUrlPatterns is non-empty, pass it as scrape_docs.excludedUrlPatterns.";

    private const string MessageReadyToScrapeForce =
        "force=true: index exists but caller requested re-ingest. Call scrape_docs (args in NextToolArgs) to refresh. " +
        "If RecommendedExcludedUrlPatterns is non-empty, pass it as scrape_docs.excludedUrlPatterns.";
```

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~IngestToolsTests" --configuration Release`

Expected: all three new tests pass.

- [ ] **Step 6: Run the full ingest-related test set**

Run: `dotnet test SaddleRAG.slnx --filter "FullyQualifiedName~IngestStatusResponseTests|FullyQualifiedName~IngestToolsTests|FullyQualifiedName~ReconToolsTests|FullyQualifiedName~LibraryProfileServiceTests" --configuration Release`

Expected: all green.

- [ ] **Step 7: Build the full solution**

Run: `dotnet build SaddleRAG.slnx --configuration Release -p:TreatWarningsAsErrors=true`

Expected: clean build.

- [ ] **Step 8: Commit**

Write `e:/tmp/recon-crawl-hints-task6.txt`:

```
Forward CrawlHints.ExcludedUrlPatterns through start_ingest

start_ingest's READY_TO_SCRAPE branch now reads
LibraryProfile.CrawlHints.ExcludedUrlPatterns and surfaces them on
IngestStatusResponse.RecommendedExcludedUrlPatterns. Message text
points the calling LLM at the new field. MakeReadyToScrape becomes
internal so it can be unit-tested directly.
```

Commit:

```
git add SaddleRAG.Mcp/Tools/IngestTools.cs SaddleRAG.Tests/Mcp/IngestToolsTests.cs
git commit -F e:/tmp/recon-crawl-hints-task6.txt
```

---

## Task 7: Full-test run and manual smoke

**Files:** none modified — verification only.

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test SaddleRAG.slnx --configuration Release`

Expected: no regressions. Any failures here indicate something earlier in the plan was incomplete — fix and amend the relevant task's commit (or, if already pushed, add a follow-up commit).

- [ ] **Step 2: Manual MCP smoke**

In an MCP-connected session, exercise the new path end-to-end:

1. Call `recon_library` for a known library and confirm the returned `Instructions` mentions both `crawlHints` and `dryrun_scrape`, and `JsonSchema` includes `excludedUrlPatterns`.
2. Submit a profile via `submit_library_profile` with a non-empty `crawlHints.excludedUrlPatterns` (e.g., `["/account/login"]`).
3. Call `start_ingest` for the same `(library, version)` and confirm the response includes `RecommendedExcludedUrlPatterns: ["/account/login"]`.
4. Confirm the message text points to the new field.

Document any deviations as follow-up issues — do not fix inline at this point.

- [ ] **Step 3: Open the PR**

Run (substituting the actual branch name):

```
gh pr create --title "Recon crawl hints" --body-file e:/tmp/recon-crawl-hints-pr.txt
```

`e:/tmp/recon-crawl-hints-pr.txt`:

```
## Summary

Adds CrawlHints to LibraryProfile so recon's calling LLM can record
crawl-scope concerns (excluded URL patterns, expected hosts, free-form
notes) at the same time it characterizes the parser. start_ingest's
READY_TO_SCRAPE branch surfaces those patterns on the response so
the next scrape_docs call gets them automatically.

Motivated by the actipro-wpf 25.1 scrape (job 6fb2231e) that considered
7.28M URLs across 2.5h and indexed nothing, because the API reference
was behind /support/account/login redirects with unique returnUrl
parameters that made every login URL look distinct to the crawler.

## Test plan

- [x] dotnet test SaddleRAG.slnx --configuration Release (full suite)
- [x] Manual MCP smoke: recon_library payload mentions crawlHints + dryrun_scrape
- [x] Manual MCP smoke: submit_library_profile persists crawlHints
- [x] Manual MCP smoke: start_ingest returns RecommendedExcludedUrlPatterns
```

---

## Out of scope for this plan (follow-ups)

These came up during design but are deliberately deferred to keep the change reviewable.

1. **Dryrun warnings.** The dryrun report already has the signals (`PagesRemainingInQueue`, `HitMaxPagesLimit`, OutOfScope sample URLs) but does not translate them into a `Warnings` field. Add a `Warnings: ["Auth-wall path detected", "Queue would exceed N URLs at depth 1"]` array on the dryrun result.
2. **start_ingest nudges dryrun.** When a fresh `READY_TO_SCRAPE` is reached for a library that has no prior dryrun, suggest dryrun in the message.
3. **Re-scrape actipro-wpf 25.1.** Once this lands, the user runs `submit_url_correction` (or `delete_version` + `scrape_docs` with a populated profile) to clean and re-scrape with `excludedUrlPatterns: ["/support/account/"]`.
4. **Hash inclusion review.** Currently `ComputeHash` excludes `CrawlHints` (deliberate — they affect crawl, not classify). If we later discover changes to crawl hints should re-trigger something, revisit the exclusion. Tracked as a comment in `ComputeHash`.

---

## Self-Review Checklist

- [x] **Spec coverage.** Every conversation point lands in a task: new profile field (Task 1), service support (Tasks 2–3), recon awareness (Task 4), response surface (Task 5), state-machine wiring (Task 6).
- [x] **Placeholders scrubbed.** No "TBD" / "implement later" / "similar to" / "etc." strings.
- [x] **Type/name consistency.** `CrawlHints` (record name), `crawlHints` (JSON key), `CrawlHints` (property name on `LibraryProfile`), `ExcludedUrlPatterns` / `excludedUrlPatterns`, `RecommendedExcludedUrlPatterns`, `MakeReadyToScrape` — verified consistent across all tasks.
- [x] **Penske standards.** Single-return preserved (existing methods); ArgumentNullException at entry (`Build` validation extended); m/sm prefixes preserved; new constants follow PascalCase; no magic numbers introduced; expression-bodied where simple (`MakeReadyToScrape`); regions match file conventions.
- [x] **Commit hygiene.** All commits use `-F` message file (per CLAUDE.md). No AI attribution. No `Co-Authored-By` trailers. No git config changes.
- [x] **Backward compatibility.** Existing v2 profiles in MongoDB deserialize cleanly because `CrawlHints` defaults to `new CrawlHints()` and Mongo's POCO deserializer leaves missing fields at default. No migration script needed.
