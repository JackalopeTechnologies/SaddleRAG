# Handoff — Scrape Pattern Analyzer Simulator

**Purpose:** This document is a complete, self-contained brief for a fresh Claude Code session tasked with building the offline simulator that validates the scrape pattern analyzer algorithm before any production implementation.

Hand the contents of this file (or its filename) to the next session. The next session needs no other context from prior brainstorming — everything required is here.

---

## What you're being asked to build

An **offline simulator** for the scrape pattern analyzer algorithm described in [`docs/superpowers/specs/2026-05-15-scrape-pattern-analyzer-design.md`](../specs/2026-05-15-scrape-pattern-analyzer-design.md). The simulator replays existing scrape audit logs through the algorithm without making any network requests or modifying production code. The goal is to validate the algorithm's verdicts (KEEP / EXCLUDE / REVIEW) against real-world data from 21 already-scraped libraries before we commit to changing the production crawler.

Read the spec first — especially these sections:
- "Pattern Signature Algorithm" (cardinality-aware templatization)
- "Probe-then-Decide Engine" (the verdict matrix)
- "Empirical Validation Plan" (this work, scoped)

The simulator is **throwaway code**. Quality bar: it works, the output is readable, but it doesn't need production-grade testing, error handling polish, or long-term maintenance. Build it in the repo's `Scratch/` directory, which is .gitignored.

---

## What success looks like

A JSON report per library, hand-eyeballed by the user, that answers: **does the algorithm produce verdicts that match intuition?**

- Known-clean libraries (`mathnet-numerics`, `mathnet-spatial`, `xunit-v3`) → expect few/no EXCLUDE verdicts, mostly KEEP-templated content trees
- Known-noisy libraries (any that today has obviously bloated chunk counts or include marketing/login-flow content) → expect clear EXCLUDE verdicts on the noise patterns, with sample URLs the user can recognize as chrome
- Mixed libraries → expect the report to surface candidate excludes the user can validate by inspection

If the verdicts don't match intuition, that signals an algorithm refinement is needed — adjust thresholds, templatization rules, or probe budget in the simulator, regenerate reports, repeat.

---

## What data exists

### MongoDB collections relevant to the simulator

Connection lives in `SaddleRAG.Mcp/appsettings.json` under `MongoDB.Profiles.local.ConnectionString` (default `mongodb://localhost:27017`) and `DatabaseName` (default `SaddleRAG`).

- **`Libraries`** — one document per library (Id, Name, Hint, CurrentVersion, AllVersions). 21 documents currently.
- **`LibraryVersions`** — one document per (libraryId, version) pair with scrape metadata (ScrapedAt, PageCount, ChunkCount, EmbeddingProvider, etc.)
- **`Jobs`** — unified job records. Each completed scrape has a `JobType=Scrape` (or `DryRunScrape`) row with an `InputJson` field containing the original `ScrapeJob` (`RootUrl`, `LibraryId`, `Version`, `AllowedUrlPatterns`, `ExcludedUrlPatterns`, etc.) and a `Result` with timing/page counts.
- **`ScrapeAudit`** — one row per (URL, ParentUrl, Job) edge. This is the gold mine. Schema in `SaddleRAG.Core/Models/Audit/ScrapeAuditLogEntry.cs`. Key fields the simulator needs:
  - `JobId`, `LibraryId`, `Version`
  - `Url`, `ParentUrl` (nullable for the seed URL)
  - `Host`, `Depth`, `DiscoveredAt`
  - `Status` (`Considered` / `Skipped` / `Fetched` / `Failed` / `Indexed`)
  - `SkipReason` (`BinaryExt` / `PatternMissAllowed` / `OffSiteDepth` / etc.)

The audit log records the discovery and disposition of every URL the crawler considered. It does NOT currently store `ContentBytes`, `LinksFound`, `ContentHash`, or pattern signatures — those are the audit log extensions the spec adds for future scrapes. The simulator works with what's there.

### Listing the libraries and their scrape jobs

```csharp
var libraryRepo = repositoryFactory.GetLibraryRepository(profile: null);
var libraries = await libraryRepo.ListAsync(ct);

var jobRepo = repositoryFactory.GetJobRepository(profile: null);
var recentScrapeJobs = await jobRepo.ListRecentAsync(JobType.Scrape, limit: 200, ct);
// Filter to one job per (libraryId, version) — most recent scrape for each library.
```

---

## What the simulator should do

For each completed `Scrape` job in the database:

1. **Pull the audit rows for that jobId** — `IScrapeAuditRepository.QueryAsync(jobId, status: null, ..., limit: int.MaxValue, ct)`.
2. **Sort chronologically** by `DiscoveredAt`.
3. **Replay** the audit row by row through the templatization + probe-state-machine logic from the spec:
   - Maintain per-key cardinality observations (`observationCount[key]`, `distinctValues[key]`)
   - Compute `urlTemplateSig` for each URL with the cardinality-aware rules
   - Maintain `probeCount[template]`, `parentsByTemplate[template]`, `frontierGrowthProbes2to5[template]`
   - For `Status=Fetched` rows, increment `probeCount`; check at 5 probes; lock verdict
4. **Output a JSON report** per library to `Scratch/PatternAnalyzerSimulator/reports/<libraryId>-<version>.json`:

```json
{
  "Library": "<libraryId>",
  "Version": "<version>",
  "JobId": "<jobId>",
  "TotalFetchedRows": <int>,
  "TotalDistinctTemplates": <int>,
  "TerminationCause": "Completed",
  "TemplatesByVerdict": {
    "EXCLUDE": [
      {
        "Template": "<templateSig>",
        "InstanceCount": <int>,
        "FanInCount": <int>,
        "FrontierGrowthProbes2to5": <int>,
        "SampleUrls": ["...", "..."],
        "SampleParentUrls": ["...", "..."],
        "DepthRange": [<min>, <max>],
        "Reason": "<human readable verdict reason>"
      }
    ],
    "KEEP": [...],
    "REVIEW": [...]
  },
  "EstimatedFetchSavings": <int>,
  "Notes": ["any warnings about missing data, unusual patterns, etc."]
}
```

5. **Also output an aggregate summary** to `Scratch/PatternAnalyzerSimulator/reports/_summary.json`:

```json
{
  "GeneratedAt": "...",
  "LibrariesProcessed": <int>,
  "PerLibrarySummary": [
    {
      "Library": "...",
      "TotalFetched": <int>,
      "ExcludeTemplateCount": <int>,
      "EstimatedFetchSavings": <int>,
      "TopExcludeTemplateSample": "<templateSig>"
    }
  ]
}
```

---

## What the simulator can't measure (and how to handle it)

The existing audit log lacks the fields the spec adds (`ContentBytes`, `LinksFound`, `ContentHash`, `PatternSigs`). That means:

- **`contentClassSig` is unavailable** — the secondary signal that distinguishes KEEP from REVIEW within the "high frontier growth" bucket can't be computed.
- **Verdict matrix simplification for the simulator:** the simulator can confidently emit `EXCLUDE` verdicts (driven by frontier growth + fan-in) and `KEEP` verdicts (driven by high frontier growth). Without `contentClassSig`, anything that would have been REVIEW under the full algorithm gets marked `REVIEW` with `Reason="Content class data not available; verdict pending full algorithm"`.

That's fine for v1 validation. The primary EXCLUDE judgment is what we're validating; the secondary signal can be validated later against fresh scrapes once the audit-log extensions ship.

---

## Build mechanics

### Where the code lives

`Scratch/PatternAnalyzerSimulator/` — a console app project. The `Scratch/` directory is .gitignored per project conventions (CLAUDE.md mentions it). No production code touched. Use any C# project skeleton (`dotnet new console -n PatternAnalyzerSimulator`).

### Dependencies

The simulator can reference production assemblies — `SaddleRAG.Core` and `SaddleRAG.Database` — for `ScrapeAuditLogEntry`, `IScrapeAuditRepository`, `RepositoryFactory`, etc. Add project references; don't re-implement MongoDB access.

### Bootstrapping the repository factory

The simulator needs a connection string. Two options:
- Read `appsettings.json` from `SaddleRAG.Mcp/`
- Or hard-code `mongodb://localhost:27017` + `SaddleRAG` in the simulator's main; it's throwaway anyway.

Use whichever is simpler. The setup pattern for `RepositoryFactory` is visible in `SaddleRAG.Mcp/Program.cs:builder.Services.AddSaddleRagDatabase(...)`. The simulator doesn't need to go through the full DI pipeline — it can construct a `MongoClient` directly and pass it to repository constructors.

### Output format

Single JSON file per library + one summary file. Use `System.Text.Json` with `WriteIndented=true`. File names should be safe for any libraryId (replace `/`, etc.).

### What NOT to do

- Don't modify `SaddleRAG.Ingestion`, `SaddleRAG.Core`, or `SaddleRAG.Mcp`. Production code stays untouched.
- Don't add tests. This is a throwaway tool.
- Don't add new MCP tools. The simulator is a console app, not part of the MCP server.
- Don't modify the audit log schema or any persisted data. The simulator is strictly read-only on the database.

---

## Algorithm details — TL;DR for the implementer

From the spec, the simulator needs to implement:

### Cardinality-aware templatization

```csharp
record TemplateExtractorState
{
    Dictionary<string, int> ObservationCount; // key → count of times key appeared
    Dictionary<string, HashSet<string>> DistinctValues; // key → set of distinct values
}

string Templatize(Uri url, TemplateExtractorState state)
{
    var path = NormalizePath(url.AbsolutePath);
    var pathSegments = path.Split('/').Select(seg =>
        IsNumeric(seg) ? "{id}" :
        IsGuid(seg) ? "{guid}" :
        IsLongHex(seg) ? "{hash}" :
        seg
    );
    var templatePath = string.Join("/", pathSegments);

    var querystring = ParseQuerystring(url.Query); // keep ordering; List<(key, value)>
    var templatedQs = querystring.Select(kv =>
    {
        var key = kv.key;
        var value = kv.value;
        state.ObservationCount[key] = (state.ObservationCount.GetValueOrDefault(key) + 1);
        state.DistinctValues.GetOrAdd(key, () => new HashSet<string>()).Add(value);

        var distinctCount = state.DistinctValues[key].Count;
        var obsCount = state.ObservationCount[key];

        if (distinctCount >= 5 && (double)distinctCount / obsCount >= 0.5)
        {
            // Templatize this value
            var placeholder =
                LooksLikeEncodedPath(value) ? "{path}" :
                IsNumeric(value) ? "{id}" :
                IsGuid(value) ? "{guid}" :
                IsLongHex(value) ? "{hash}" :
                "{value}";
            return $"{key}={placeholder}";
        }
        else
        {
            // Keep distinct
            return $"{key}={value}";
        }
    });

    var qsString = templatedQs.Any() ? "?" + string.Join("&", templatedQs) : "";
    return $"{url.Scheme}://{url.Host.ToLowerInvariant()}{templatePath}{qsString}";
}
```

### Probe state machine

```csharp
record ProbeState
{
    int ProbeCount;
    HashSet<string> ParentsByTemplate;
    int FrontierGrowthProbes2to5;
    Verdict Verdict;  // Unlocked, Keep, Exclude, Review
    List<string> SampleUrls;
    List<string> SampleParentUrls;
    int MinDepth, MaxDepth;
}

enum Verdict { Unlocked, Keep, Exclude, Review }

// As you replay audit rows in chronological order:
void OnFetchedRow(ScrapeAuditLogEntry row, GlobalState state)
{
    var template = Templatize(new Uri(row.Url), state.TemplateState);
    var probeState = state.ProbeStates.GetOrAdd(template, () => new ProbeState());

    probeState.ProbeCount += 1;
    if (row.ParentUrl != null)
        probeState.ParentsByTemplate.Add(row.ParentUrl);
    probeState.MinDepth = Math.Min(probeState.MinDepth, row.Depth);
    probeState.MaxDepth = Math.Max(probeState.MaxDepth, row.Depth);

    if (probeState.SampleUrls.Count < 5)
        probeState.SampleUrls.Add(row.Url);
    if (row.ParentUrl != null && probeState.SampleParentUrls.Count < 5)
        probeState.SampleParentUrls.Add(row.ParentUrl);

    // Frontier growth: count outbound links from this row's URL that go to
    // brand-new URLs the crawler hadn't seen yet.
    // The audit log doesn't directly store "what links this URL produced," but we can
    // reconstruct: every audit row with ParentUrl == row.Url is an outbound link
    // from this page. We have to know whether each was novel at this moment in time.
    //
    // Simpler reconstruction: as we replay rows chronologically, maintain a
    // visited/queued set. When we see a row whose ParentUrl matches a URL we've
    // already processed as a probe, that row represents an outbound link from that
    // probe. If row.Url was unseen (not yet in visited/queued) at the time the
    // ParentUrl was probed, the frontier grew.
    //
    // Detail: see step-by-step replay below.

    if (probeState.ProbeCount == 5 && probeState.Verdict == Verdict.Unlocked)
    {
        probeState.Verdict = ComputeVerdict(probeState, state.TotalFetched);
    }
}
```

### Verdict matrix (simplified for simulator without contentClassSig)

```
if (probeState.FrontierGrowthProbes2to5 < 5):
    return Verdict.Exclude  // Worthless
else if (probeState.ParentsByTemplate.Count / state.TotalFetched >= 0.5
         AND probeState.FrontierGrowthProbes2to5 < 20):
    return Verdict.Exclude  // High fan-in chrome, modest growth
else:
    return Verdict.Review  // Without contentClass, can't confidently say Keep
```

The simulator's `Review` bucket will be larger than the full algorithm's would be, since we can't distinguish version-selector-like cases from genuine content. That's expected and called out in the report's `Notes`.

### Replay structure

```csharp
async Task<LibraryReport> SimulateLibrary(string jobId)
{
    var rows = await auditRepo.QueryAsync(jobId, ..., limit: int.MaxValue, ct);
    var sortedRows = rows.OrderBy(r => r.DiscoveredAt).ToList();

    var state = new GlobalState();

    foreach (var row in sortedRows)
    {
        // Step 1: did the parent's template observe a frontier growth event from this row?
        if (row.ParentUrl != null && state.ProbeStatesByUrl.TryGetValue(row.ParentUrl, out var parentTemplate))
        {
            var parentProbeState = state.ProbeStates[parentTemplate];
            // Probes 2-5 of the parent template, when row.Url was previously unseen
            if (parentProbeState.ProbeCount >= 2 && parentProbeState.ProbeCount <= 5
                && !state.AlreadySeenUrls.Contains(row.Url))
            {
                parentProbeState.FrontierGrowthProbes2to5 += 1;
            }
        }

        state.AlreadySeenUrls.Add(row.Url);

        // Step 2: if this row was actually fetched, process it as a probe
        if (row.Status == AuditStatus.Fetched || row.Status == AuditStatus.Indexed)
        {
            var template = Templatize(new Uri(row.Url), state.TemplateState);
            state.ProbeStatesByUrl[row.Url] = template;
            state.TotalFetched += 1;

            OnFetchedRow(row, state);
        }
    }

    // Periodically re-templatize would happen here for cardinality-flip handling;
    // for v1 simulator, skip this — single-pass templatization is fine for the
    // primary validation question (does EXCLUDE correctly catch chrome?)

    return BuildReport(state);
}
```

The "Step 1" frontier-growth detection is the critical piece. It says: when the audit log produces a row (`URL`, `ParentUrl`), that row represents the moment the crawler discovered `URL` as an outbound link from `ParentUrl`. If `ParentUrl` was a recent probe of some template, and `URL` is brand-new at this moment in time, the parent's frontier grew.

---

## What to report back to the user

After running the simulator across the 21 libraries:

1. **The reports themselves** (`Scratch/PatternAnalyzerSimulator/reports/*.json`)
2. **A spot-check verbal summary** — pick 3-5 libraries and report:
   - Top EXCLUDE templates with their reasons + sample URLs
   - Top KEEP templates
   - Anything surprising

3. **A recommendation:**
   - "Algorithm looks sound, EXCLUDE verdicts match intuition for chrome/noise patterns" → spec moves toward Approved
   - "Algorithm needs adjustment because <specific finding>" → propose threshold changes; show updated reports

4. **Update the spec doc** in `docs/superpowers/specs/2026-05-15-scrape-pattern-analyzer-design.md` if validation revealed needed changes:
   - Threshold adjustments (probe budget, fan-in cutoff, etc.)
   - New edge cases discovered
   - Any algorithm refinements

When the user is satisfied, the next step (in yet another session) is to invoke `writing-plans` skill to produce the implementation plan for the production crawler changes.

---

## Branch / commit guidance

Per project rules in `CLAUDE.md`:

- **NEVER commit directly to master.** Branch from current master before any commits.
- **No AI attribution in commit messages or PR bodies.** No `Co-Authored-By: Claude ...` trailers. No "🤖 Generated with Claude Code" lines. Commit messages contain only content the user wrote or approved.
- **Use a commit message file**, not inline `-m`: `git commit -F some-file.txt`.
- **Git identity is auto-configured per repo path** via `~/.gitconfig` includeIf. For `E:/GitHub/` repos the identity resolves to `douglas@jackalopetechnologies.com`. Don't `git config` anything.
- **Scratch directory is .gitignored**, so the simulator code itself won't be committed. Only commit changes to `docs/superpowers/specs/2026-05-15-scrape-pattern-analyzer-design.md` if the validation reveals spec updates.
- **Open a PR** if you do end up with committable changes. Title and body should be the user's content (no AI attribution).

---

## Open spec questions the validation pass should specifically check

Hand back answers to these as part of the recommendation:

1. **Is `5 probes` the right budget?** Maybe 3 is enough for noise; maybe 8 is needed to avoid premature locks on small templates.
2. **Is the cardinality threshold (`5 distinct values AND ratio ≥ 0.5`) right?** Could be too aggressive (collapsing real content variance) or too lax (missing parameter-encoder patterns).
3. **Is the 80% safety cap appropriate?** Could be 70%, could be 90% — should be informed by what happens at the cap in real data.
4. **Does the longest-common-prefix rule for `RecommendedAllowedUrlPatterns` work well?** Are there libraries where it produces a useless or over-broad allowlist?
5. **Are there pattern shapes we missed?** Anything in the 21-library corpus the algorithm consistently mis-handles?

---

## Files referenced in this handoff

- Spec: `docs/superpowers/specs/2026-05-15-scrape-pattern-analyzer-design.md`
- Audit schema: `SaddleRAG.Core/Models/Audit/ScrapeAuditLogEntry.cs`
- Audit access: `SaddleRAG.Core/Interfaces/IScrapeAuditRepository.cs`
- Repository factory pattern: see `SaddleRAG.Mcp/Program.cs` lines around `AddSaddleRagDatabase`
- Existing scrape skill: `docs/skills/scrape-library-with-saddlerag.md` (untracked at time of handoff; commit if working with it)
- Project rules: `CLAUDE.md` at repo root and `~/.claude/CLAUDE.md` for the user's global preferences
