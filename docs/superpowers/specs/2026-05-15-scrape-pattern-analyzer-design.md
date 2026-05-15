# Scrape Pattern Analyzer Design

**Date:** 2026-05-15
**Status:** Draft — blocked on empirical validation via offline simulator

## Goal

Eliminate wasted crawl time and storage on duplicate, chrome, and worthless URL families when scraping large or messy documentation sites. The current crawler fetches every unique URL string within scope and depth budgets, which causes:

- Login flows fetched hundreds of times when the login URL encodes the return path (`/login?returnUrl=/A`, `/login?returnUrl=/B`, ...)
- Header / footer chrome (contact, blog, products, privacy) fetched from every parent page that links to it
- Querystring variants of the same canonical page fetched repeatedly when the parameter doesn't change content (`?utm_source=...`, `?ref=...`)
- Search-result pages, listing-pagination, and other low-information templates explored exhaustively

Each of these is one of the same underlying pattern: **traversing this URL family produces no new information after the first few visits.** The design here detects that pattern inline during a crawl and auto-excludes such families, with an override mechanism for the rare cases where the heuristic is wrong.

## Background

The crawler today applies three filters before fetching a URL:

1. **URL string dedup** — `PageCrawler.Visited` ConcurrentDictionary prevents fetching the same literal URL twice
2. **Allowlist / exclude regex filter** — `allowedUrlPatterns` / `excludedUrlPatterns` constrain crawl scope
3. **Canonical-hash storage dedup** (PR #45) — duplicate PageRecords are not persisted when canonical URLs match

These catch exact-match repeats but miss family-level redundancy. A site that encodes the parent URL into every chrome link produces hundreds of unique URL strings all leading to the same page. Layers 1-3 cannot detect this pattern; the crawler dutifully fetches each variant.

The 2026-05-14 audit identified this as the dominant cost driver for vendor doc sites (Actipro, SciChart, etc.) and the reason real-world scrapes can stretch to multiple hours when novelty-stall detection could have terminated them in minutes.

---

## Architecture

The design adds a fourth filter layer to the crawler — **template-frontier-growth dedup** — that operates on URL families rather than individual URLs. The layer composes with the existing three without modification to them.

```
Link discovered (parent → child URL)
   │
   ├─→ Layer 1: already visited / queued? ───────────→ SKIP (existing)
   │
   ├─→ Layer 2: matches allow / exclude patterns? ───→ SKIP if filtered (existing)
   │
   ├─→ Layer 4: template verdict EXCLUDE? ──────────→ SKIP (NEW — audit SkipReason=AutoExcluded)
   │
   ▼
Queue for fetch ──→ Fetch ──→ Extract outbound links (back to top)
                                        │
                                        └─→ Update template state (probe count, frontier growth)
                                                │
                                                └─→ When 5th probe of a template completes,
                                                    compute verdict (KEEP / EXCLUDE / REVIEW)
                                                    and lock for subsequent decisions.
   │
   └─→ Emit PageRecord ──→ Layer 5: canonical-hash storage dedup (existing, PR #45)
```

Three new pieces:

1. **`TemplateExtractor`** — given a URL, computes a `templateSig` with path-segment ID/GUID/hex replacement and cardinality-aware querystring value templatization. Same input always yields the same template.
2. **`ProbeStateMachine`** — for each distinct `templateSig` encountered, tracks probe count, parents seen (fan-in), and frontier growth across probes 2-5. After the 5th probe, locks a verdict.
3. **`analyze_scrape_patterns` MCP tool** — post-hoc report tool that reads any scrape job's audit log and renders the layer-4 verdicts plus supporting evidence for LLM consumption.

`scrape_docs` and `dryrun_scrape` gain two optional parameters:

| Param | Default | Meaning |
|---|---|---|
| `autoExcludeWorthlessTemplates` | `false` | Enable inline layer-4 auto-skip. Templates that lock verdict EXCLUDE during the crawl are skipped from that point forward. |
| `autoExcludeOverrides` | `[]` | Regex strings; URLs matching any override are always fetched regardless of layer-4 verdict. Recovery mechanism for misjudged templates. |

The post-hoc analyzer is read-only and operates on the audit log after the fact. It is useful for:
- Inspecting what was auto-excluded during a scrape and why
- Retrospective analysis of scrapes that did not use auto-exclude
- Iterative workflows where the LLM examines a dry-run before committing

---

## Pattern Signature Algorithm

For every fetched URL, the crawler computes three signatures. All three are pure string operations.

### `canonicalPathSig`

`{scheme}://{lowercase-host}{normalized-path}` — querystring and fragment stripped, trailing slash normalized, case-normalized. Same definition used by the canonical-hash storage dedup from PR #45.

```
https://www.example.com/Docs/index?v=24.1#section → https://www.example.com/docs/index
```

### `urlTemplateSig`

`canonicalPathSig` with each path segment classified and ID-like segments replaced:

- Pure numeric (`[0-9]+`) → `{id}`
- GUID (`8-4-4-4-12` hex pattern) → `{guid}`
- Long hex string (`[0-9a-f]{16,}`) → `{hash}`
- Everything else → kept verbatim

Then querystring values are added back, with **per-key cardinality-aware templatization**:

For each querystring key observed across all URLs seen so far in the crawl, the analyzer maintains:
- `observationCount[key]` — number of times the key appeared
- `distinctValues[key]` — set of distinct values seen for that key

When constructing a `urlTemplateSig` for a URL with querystring keys, each value is templatized iff **all** of these hold:
- `distinctValues[key].Count >= 5` (enough samples to make a cardinality judgment)
- `distinctValues[key].Count / observationCount[key] >= 0.5` (the key behaves like a per-page parameter, not a content selector)

Otherwise, the value is kept verbatim (treats low-cardinality keys as content variance markers).

Value shape determines the placeholder:
- URL-decoded value starts with `/` and contains additional `/` → `{path}`
- Matches GUID regex → `{guid}`
- All digits → `{id}`
- 16+ hex chars → `{hash}`
- Otherwise → `{value}`

**Worked examples:**

| Observed URLs | Cardinality | Resulting `urlTemplateSig` |
|---|---|---|
| `/docs/index?v=24.1`, `?v=23.1`, `?v=22.1`, `?v=21.1` | `v`: 4 distinct values, low cardinality | `/docs/index?v=24.1` (and 23.1, 22.1, 21.1 — each kept distinct) |
| `/login?returnUrl=/A`, `/login?returnUrl=/B`, … 50 distinct | `returnUrl`: 50 distinct values, ratio ≈ 1.0 | `/login?returnUrl={path}` (all 50 collapse to one signature) |
| `/api/users/123/profile`, `/users/456/profile`, … | path numeric → `{id}` | `/api/users/{id}/profile` |
| `/search?q=foo`, `?q=bar`, … 80 distinct | `q`: 80 distinct values, ratio ≈ 1.0 | `/search?q={value}` |
| `/docs/index?page=1&sort=asc`, `?page=2&sort=asc`, … `?page=N&sort=asc` | `page`: many distinct, `sort`: 1 distinct | `/docs/index?page={id}&sort=asc` |

Cardinality observation runs continuously — the signature for a URL fetched early in the crawl may be recomputed if later observations change its key's cardinality classification. The analyzer flushes the recomputation periodically (every N=50 pages) so probe accumulation uses up-to-date signatures.

### `contentClassSig`

`{title}|{contentBytesBucket}` — title with `|` characters replaced by `_`, content-bytes bucket rounded down to the nearest 256 bytes.

```
Title: "Sign In - Actipro", content: 4112 bytes  →  "Sign In - Actipro|3840"
Title: "WPF Bars / Controls",  content: 14820 bytes →  "WPF Bars / Controls|14592"
```

Empty title → `""`; zero-byte content → bucket `0`. Always well-formed; never throws.

---

## Probe-then-Decide Engine

### Per-template state

For each `templateSig` encountered, the engine maintains:

```
probeCount             : int             // number of FETCHED URLs matching this template
parentsByTemplate      : HashSet<Url>    // distinct parents that linked to URLs matching this template
frontierGrowthProbes2to5 : int           // cumulative count of brand-new URLs added to queue
                                         // by probes 2 through 5
contentClassesObserved : HashSet<contentClassSig>  // distinct content classes seen in probes
outboundTemplatesObserved : HashSet<templateSig>   // distinct outbound link templates seen in probes
verdict                : enum { Unlocked, Keep, Exclude, Review }
```

### Probe lifecycle

When the crawler fetches a URL `U`:

1. `T = templateSig(U)` (with up-to-date cardinality)
2. `probeCount[T] += 1`
3. For each outbound link `L` extracted from the page:
   - `parentsByTemplate[templateSig(L)].Add(U)`
   - If `L` is not in the visited set and not in the queue AND `probeCount[T] >= 2`:
     - `frontierGrowthProbes2to5[T] += 1` (when `probeCount[T] <= 5`)
4. `contentClassesObserved[T].Add(contentClassSig(U))`
5. For each outbound link `L`: `outboundTemplatesObserved[T].Add(templateSig(L))`
6. If `probeCount[T] == 5` and `verdict[T] == Unlocked`: compute verdict per the matrix below

### Verdict matrix

| `frontierGrowthProbes2to5[T]` | `contentClassesObserved[T].Count` | `parentsByTemplate[T].Count / totalFetched` | Verdict |
|---|---|---|---|
| `< 5` (the 4 follow-up probes contributed almost nothing to the frontier) | (any) | (any) | **EXCLUDE** |
| `>= 5` | `1` (every probe rendered the same content class) | `>= 0.5` (linked from at least half of fetched pages) | **EXCLUDE** — high-fan-in chrome with no content variance |
| `>= 5` | `>= 2` (content varies) | `< 0.5` | **KEEP** — leaf content with downstream structure |
| `>= 5` | `1` | `< 0.5` | **REVIEW** — same content under different links; uncommon, surface to LLM |
| `>= 5` | `>= 2` | `>= 0.5` | **REVIEW** — version-selector-like: content varies, but linked from many parents; LLM decides whether redundancy is worth the storage |

Once verdict is locked, all subsequent URLs whose `templateSig == T` skip the fetch when verdict is `EXCLUDE`. `KEEP` and `REVIEW` verdicts allow fetching normally.

### Safety caps

- **Never auto-exclude the seed.** The original `rootUrl` is never excluded regardless of verdict.
- **Never auto-exclude more than 80% of fetched pages.** Tracked across all template verdicts; if locking a new EXCLUDE would push the total over 80%, the verdict is downgraded to REVIEW and logged.
- **Override precedence.** A URL matching any regex in `autoExcludeOverrides` is fetched regardless of verdict and audited normally.

### Minimum scrape size

The engine requires at least 5 probes per template to lock a verdict. On very small scrapes (under 20 pages total), the engine essentially does nothing — every template is `Unlocked` for the whole crawl. That is the desired behavior; auto-exclude on a 12-page scrape has no benefit.

---

## Audit Log Extensions

`ScrapeAuditLogEntry` gains optional fields. All nullable for backwards compatibility — pre-this-design audit rows continue to deserialize.

| Field | Type | Populated when |
|---|---|---|
| `ContentBytes` | `int?` | `Status = Fetched / Indexed` |
| `LinksFound` | `int?` | `Status = Fetched / Indexed` |
| `ContentHash` | `string?` | `Status = Fetched / Indexed` (SHA-256, reuses real-scrape path's existing hash) |
| `PatternSigs` | `PatternSignatures?` | `Status = Fetched / Indexed` |

New nested record:

```csharp
public sealed record PatternSignatures
{
    public required string CanonicalPath { get; init; }
    public required string UrlTemplate { get; init; }
    public required string ContentClass { get; init; }
}
```

`AuditSkipReason` enum gains:

- `AutoExcluded` — set when layer-4 verdict EXCLUDE skipped this fetch. `SkipDetail` records the template signature that triggered the skip.

Computation cost: three string operations per fetched page (negligible compared to Playwright render time). Storage cost: ~200 bytes added per audit row.

The new fields are always populated when present, regardless of whether `autoExcludeWorthlessTemplates` is on. This means the post-hoc `analyze_scrape_patterns` tool can run on any scrape from this design forward, including scrapes that did not enable auto-exclude.

---

## Tool Surface

### Modified — `scrape_docs` and `dryrun_scrape`

Two new optional parameters on both:

| Param | Type | Default | Description |
|---|---|---|---|
| `autoExcludeWorthlessTemplates` | `bool` | `false` | Enable inline layer-4 auto-skip during the crawl. |
| `autoExcludeOverrides` | `string[]` | `[]` | Regex strings; URLs matching any override are always fetched, bypassing any auto-exclude verdict. |

When `autoExcludeWorthlessTemplates = true`, the scrape Result JSON additionally includes:

```json
{
  "AutoExcludedTemplates": [
    {
      "TemplateSig": "/account/login?returnUrl={path}",
      "VerdictReason": "Linked from 47 of 50 fetched pages; frontier growth 0 across probes 2-5",
      "FetchSkipCount": 247,
      "SampleSkippedUrls": ["...", "..."],
      "SampleParentUrls": ["...", "..."]
    },
    ...
  ]
}
```

The LLM can inspect this list after the scrape completes and decide whether any auto-exclusion was misjudged. Recovery is a single re-scrape:

```
scrape_docs(
  url=...,
  libraryId=..., version=...,
  autoExcludeWorthlessTemplates=true,
  autoExcludeOverrides=["<the misjudged template's regex>"],
  force=true
)
```

### New — `analyze_scrape_patterns`

```csharp
[McpServerTool(Name = "analyze_scrape_patterns")]
public static Task<string> AnalyzeScrapePatterns(
    RepositoryFactory repositoryFactory,
    [Description("Job id of a completed scrape_docs or dryrun_scrape job")]
    string jobId,
    [Description("Optional database profile name")] string? profile = null,
    [Description("Limit returned templates per verdict bucket (default 50)")]
    int topN = 50,
    CancellationToken ct = default)
```

Read-only. Replays the audit log for `jobId` through the probe-then-decide engine and returns a `PatternAnalysisReport`. Works on any scrape job with `PatternSigs`-populated audit rows. Distinguishes:

- **`ActiveDuringScrape`** — patterns the scrape itself excluded (because `autoExcludeWorthlessTemplates` was on)
- **`RecommendedForNextScrape`** — patterns the analyzer detects now but were not excluded (because `autoExcludeWorthlessTemplates` was off, or the verdict locked too late in the crawl)

Sample shape:

```json
{
  "JobId": "...",
  "Library": "actipro-wpf",
  "Version": "validation",
  "Summary": {
    "TotalPagesFetched": 1240,
    "DistinctTemplates": 87,
    "ActiveDuringScrape": 0,
    "RecommendedForNextScrape": 12,
    "EstimatedFetchSavings": 487
  },
  "Templates": [
    {
      "Layer": "FrontierGrowth",
      "TemplateSig": "/account/login?returnUrl={path}",
      "InstancesObserved": 247,
      "FanInCount": 247,
      "FrontierGrowthProbes2to5": 0,
      "ContentClassesObserved": 1,
      "DepthRange": [1, 6],
      "SampleUrls": ["...", "..."],
      "SampleParentUrls": ["...", "..."],
      "Verdict": "EXCLUDE",
      "VerdictReason": "Frontier growth 0 across probes 2-5; high fan-in (99%) and constant content class",
      "Status": "RecommendedForNextScrape",
      "SuggestedRegex": "^https://www\\.actiprosoftware\\.com/account/login(\\?.*)?$"
    },
    {
      "Layer": "FrontierGrowth",
      "TemplateSig": "/docs/wpf/index?v={value}",
      "InstancesObserved": 4,
      "FanInCount": 4,
      "FrontierGrowthProbes2to5": 80,
      "ContentClassesObserved": 1,
      "Verdict": "REVIEW",
      "VerdictReason": "High frontier growth but content classes collapse to one; possible version-selector redundancy",
      "Status": "RecommendedForNextScrape",
      "SuggestedRegex": null
    }
  ],
  "RecommendedExcludedUrlPatterns": [
    "^https://www\\.actiprosoftware\\.com/account/login(\\?.*)?$",
    ...
  ],
  "RecommendedAllowedUrlPatterns": [
    "^https://www\\.actiprosoftware\\.com/docs/controls/wpf/"
  ],
  "RelatedSubdomainsFiltered": [
    {
      "Host": "support.actiprosoftware.com",
      "ConsideredCount": 465,
      "SampleUrls": ["...", "..."]
    }
  ],
  "NextStepHint": "Apply RecommendedExcludedUrlPatterns + RecommendedAllowedUrlPatterns; re-scrape with autoExcludeWorthlessTemplates=true."
}
```

### Regex generation for `SuggestedRegex`

Tier 1 — Canonical-path or template-derived regex (always tried first):
- Take the template signature, regex-escape, expand placeholders:
  - `{id}` → `\d+`
  - `{guid}` → `[0-9a-f-]{36}`
  - `{hash}` → `[0-9a-f]{16,}`
  - `{path}` → `[^&]*`
  - `{value}` → `[^&]*`

Tier 2 — Longest common path substring across sample URLs (fallback when Tier 1 yields a regex that does not match its own samples):
- Compute longest common substring of the path portion of all sample URLs
- If length ≥ 6 and substring is not the host portion: regex-escape and emit
- Wrap in `(/|\?|$)` boundary

Tier 3 — Verdict downgrade to REVIEW with `SuggestedRegex = null` when neither tier yields a regex that matches all samples without matching more than 80% of fetched URLs.

The analyzer self-validates every generated regex before emitting: must compile, must match all sample URLs, must not match more than 80% of fetched URLs.

### `RecommendedAllowedUrlPatterns` generation

Compute longest common prefix across all URLs whose templates have verdict `KEEP`. Emit when:
- ≥80% of KEEP-verdict URLs share a prefix
- Prefix length ≥10 characters
- Prefix is not the host portion alone

Useful for multi-product vendor sites where the docs of interest live under a known subtree (e.g., `/docs/controls/wpf/`).

### `RelatedSubdomainsFiltered`

Read from audit `SkipReason == PatternMissAllowed` entries, group by host, surface any subdomain of the root URL's host with consider count ≥ 20. Lets the LLM decide whether to broaden the allowlist (e.g., if `support.foo.com` has linked content the docs would benefit from).

---

## Skill Workflow Integration

The `scrape-library-with-saddlerag` skill gains a new recipe section.

### Recipe — Inline auto-skip (the default for unknown sites)

```
1. scrape_docs(
     url=<root>,
     libraryId=<id>, version=<ver>,
     autoExcludeWorthlessTemplates=true
   )
2. Inspect the scrape Result's AutoExcludedTemplates field.
3. If anything was wrongly excluded:
      scrape_docs(
        ..., force=true,
        autoExcludeWorthlessTemplates=true,
        autoExcludeOverrides=[<misjudged regex>]
      )
4. Validate with the usual confirming-scrape recipe.
```

### Recipe — Cautious mode (when stakes warrant pre-commit inspection)

```
1. dryrun_scrape(url=<root>, library=<id>, version=<ver>,
                 autoExcludeWorthlessTemplates=true)
2. analyze_scrape_patterns(jobId=<from step 1>)
3. Inspect the report. Pick or override RecommendedExcludedUrlPatterns / RecommendedAllowedUrlPatterns as appropriate.
4. scrape_docs(url=<root>,
              libraryId=<id>, version=<ver>,
              excludedUrlPatterns=<chosen excludes>,
              allowedUrlPatterns=<chosen allows>,
              autoExcludeWorthlessTemplates=false)
5. Validate.
```

Inline mode is the recommended default. Cautious mode is for first-time scrapes of large vendor portals where storage and time cost is significant and the LLM wants to vet patterns before committing index storage.

### Tool reference additions

| Tool | Required params | When to use |
|---|---|---|
| `analyze_scrape_patterns` | `jobId` | Post-process any scrape or dry-run job's audit into a `PatternAnalysisReport`. Read-only; callable repeatedly. |

### Mental model addition

```
Two new knobs on scrape_docs / dryrun_scrape:

| Knob | Default | What it does |
|---|---|---|
| autoExcludeWorthlessTemplates | false | Enable inline layer-4 auto-skip. Templates whose 5-probe verdict locks EXCLUDE get skipped from that point forward. |
| autoExcludeOverrides | [] | Patterns the LLM wants protected from auto-exclusion even if heuristics flag them. Regex strings; matches always fetched. |
```

### Gotchas section additions

- The 5-probe verdict requires at least 5 fetched instances of a template. Scrapes under ~20 pages effectively never trigger auto-exclude. That is intended behavior; small scrapes don't benefit.
- The 80% safety cap means auto-exclude can never remove more than 80% of fetched pages. If your scrape comes back with very few pages and high `AutoExcludedTemplates`, the cap probably saved you from a misconfiguration — inspect the verdict reasons before adding overrides.
- `autoExcludeOverrides` regex strings are matched against the full URL. Anchor with `^` and escape regex metacharacters; the analyzer validates compilability but not intent.

---

## Layer Model Interaction (summary)

| Layer | Mechanism | Operates on | Stage | Source |
|---|---|---|---|---|
| 1 | URL-string dedup | Literal URL strings | Pre-queue | Existing |
| 2 | Allow / exclude regex | URL strings | Pre-queue | Existing |
| 4 | Template-frontier-growth | `templateSig` families | Pre-fetch | NEW (this design) |
| 5 | Canonical-hash storage dedup | Canonical URL hash | Post-fetch, pre-write | Existing (PR #45) |

Layer 3 (canonical-path fetch dedup, briefly considered in design discussion) is intentionally NOT included. Layer 4's template extraction is finer-grained and subsumes everything Layer 3 would have done.

Mutual benefit between Layers 1 and 4:
- Layer 1 prevents Layer 4's probe budget from being spent on URL repeats (each URL counts as at most one probe)
- Layer 4 prevents Layer 1's queue from growing unboundedly with novel chrome URLs (post-verdict, new chrome URLs never reach the queue)

---

## Error Handling and Edge Cases

### Crawler-side

- **Failed fetches do not count as probes.** Only successful fetches with computed signatures contribute to `probeCount` or `frontierGrowthProbes2to5`. A network error during a probe does not lock a premature verdict.
- **Cancellation mid-crawl.** The audit rows already written are preserved; the scrape's Result includes whatever `AutoExcludedTemplates` had locked verdicts. `analyze_scrape_patterns` operates correctly on partial audits.
- **Signature computation never throws.** Empty title, zero-byte content, non-UTF-8 content all produce well-formed signatures.
- **Title with `|` character** is replaced with `_` before joining into `contentClassSig`.

### Analyzer-side

- **Job not found** → `{"Error": "Job '<id>' not found", "Status": "NotFound"}`.
- **Empty audit** → minimal report with `Summary.TotalPagesFetched = 0` and a `NextStepHint` directing the LLM to `get_job_status`.
- **Audit without `PatternSigs`** (pre-this-design scrapes) → top-level `LimitedAnalysis = true` flag; analyzer falls back to canonical-path-only inference from raw URLs in audit rows.
- **Regex generation failure** (Tiers 1 and 2 both yield invalid or over-broad regex) → verdict downgrades to `REVIEW`, `SuggestedRegex = null`. Analyzer logs a warning row.
- **80% safety cap fires during analysis** → all verdicts that would have been EXCLUDE but pushed the total over 80% downgrade to REVIEW. Top-level `SafetyCapTriggered = true` set in the report.

### Cardinality classification timing

A querystring key seen 5 times early in a crawl might be classified low-cardinality and have its values kept distinct. If the key is later observed many more times with new values, its classification may flip to high-cardinality. The engine handles this by periodically (every 50 fetched pages) re-templatizing **every URL in the probe state map** under the current cardinality classifications and recomputing each template's probe stats. Verdicts locked under now-stale classifications get rechecked; if a verdict no longer matches the recomputed stats, it is downgraded to `Unlocked` and re-evaluated when probe count next reaches 5.

### Override conflict

If a URL matches both an `autoExcludeOverrides` regex AND a locked EXCLUDE template, the override wins (the URL is fetched). Audit records `Status=Fetched, SkipDetail=OverrideAppliedToExcludedTemplate=<template>`.

---

## Testing Strategy

### Unit — `TemplateExtractor`

Table-driven xUnit tests:
- Path-segment templatization (numeric, GUID, hex, ambiguous)
- Querystring cardinality observation (low-cardinality kept distinct, high-cardinality templatized)
- Value-shape placeholder selection (`{path}` vs `{id}` vs `{value}`)
- Re-templatization on cardinality flip
- `contentClassSig` edge cases (empty title, zero bytes, pipe in title)

### Unit — `ProbeStateMachine`

Table-driven tests with synthetic outbound-link generators:
- Probe count increments only on successful fetches
- Frontier growth counts only brand-new URLs added during probes 2-5
- Verdict matrix coverage (every cell of the table tested)
- Override precedence
- 80% safety cap enforcement
- Verdict re-evaluation on cardinality flip

### Unit — `analyze_scrape_patterns` heuristics

Synthetic audit-log inputs:
- Login-loop scenario (50 URLs, same content class, fan-in ~1.0, frontier growth 0)
- Version-selector scenario (5 URLs, content classes vary, frontier growth high)
- REST API scenario (templated path, unique content per instance, frontier growth high)
- Cross-product pollution scenario (multi-subtree, allowlist generation)
- Subdomain considers scenario (`RelatedSubdomainsFiltered` populated)

Regex generator self-validation: every emitted regex compiles, matches all samples, does not over-match (>80% of fetched).

### Integration — fixture site

New static-HTML fixture under `SaddleRAG.Tests/TestData/PatternFixtures/`, served via the in-test HTTP listener. The fixture exercises every pattern shape:
- Real content pages with distinct titles and sizes
- Login form linked from every other page with returnUrl encoding
- Search-results page with high-cardinality `q` parameter
- Versioned doc page with low-cardinality `v` parameter
- REST-style API endpoints with templated path
- Header chrome (about / contact / privacy) linked from every page

Tests:
- Run `dryrun_scrape` with `autoExcludeWorthlessTemplates=true` against the fixture
- Assert specific verdicts on specific template signatures
- Assert `RecommendedExcludedUrlPatterns` includes the login regex and chrome regexes
- Apply the recommended excludes in a second dry-run; assert page count drops and the new dry-run's `Summary.RecommendedForNextScrape` count is lower than the first run's

### Backwards-compatibility

Audit rows without `PatternSigs` deserialize cleanly. `analyze_scrape_patterns` against such audits returns `LimitedAnalysis = true` with canonical-path-only inference.

### Property-style

For any synthetic audit input, `RecommendedExcludedUrlPatterns` applied as `excludedUrlPatterns` against the original URL set never excludes more than 80% — even on adversarially-shaped inputs. xUnit with a small synthetic-audit generator.

### What we don't test in CI

Real-world sites (Actipro, mathnet, etc.) — flaky, slow, externally dependent. Empirical validation against the 21-library corpus is a one-off offline pass (see next section), not a CI gate.

---

## Empirical Validation Plan

Before committing to production crawler changes, validate the algorithm against the 21 already-scraped libraries. The validation tool is an **offline simulator**: a small standalone C# program that consumes existing MongoDB audit logs and replays the algorithm offline, no production code changes required.

### What the simulator does

1. Connect to MongoDB, read `ScrapeAuditLogEntry` rows for a given `jobId`
2. Sort chronologically by `DiscoveredAt`
3. Replay the discovery events through a `TemplateExtractor` + `ProbeStateMachine`
4. For each library, emit a JSON report:
   - Per-template: instance count, fan-in count, frontier growth, verdict
   - EXCLUDE / KEEP / REVIEW buckets with sample URLs
   - Estimated fetch savings if `autoExcludeWorthlessTemplates` had been on

### What the simulator can measure

- **Frontier-growth signal** (primary EXCLUDE driver) — fully measurable from existing audit data
- **Fan-in count per template** — fully measurable
- **Template extraction correctness** — observable via the produced template list

### What the simulator cannot measure

- **Content overlap signal** (`contentClassSig` distinctness across probes) — requires `ContentBytes` and title in audit, which today's audit log does not store
- **Outbound-link novelty fan-out** — partially measurable (audit has per-row ParentUrl), but coarse

The missing signals are secondary. The primary EXCLUDE judgment (frontier growth) is what we need to validate first.

### Validation pass

Across the 21 libraries, manually eyeball:
- Did known-noisy libraries (Actipro-style, vendor doc portals) get clear EXCLUDE verdicts on their chrome templates? If not — algorithm needs adjustment.
- Did known-clean libraries (mathnet-numerics, mathnet-spatial, etc.) get few or no EXCLUDE verdicts? If many KEEP-verdict templates got EXCLUDED — algorithm is too aggressive.
- For libraries with version selectors or other content-variant patterns — what bucket did they land in? (Probably REVIEW given the limited audit data; that's correct.)

### Iteration loop

If the simulator surfaces problems:
- Adjust thresholds (5-probe → 7? 80% safety cap → 70%?)
- Adjust cardinality classification rule (5 distinct values → 10?)
- Tighten or loosen value-shape placeholder rules

Iterate against the same 21 libraries until verdicts match intuition. Then write the production implementation plan.

### Build mechanics

The simulator lives outside production code — `Scratch/PatternAnalyzerSimulator/` as a throwaway console app referencing `SaddleRAG.Core` for repository access. Output is JSON files per library, easy to diff and inspect. No MCP integration, no tests beyond manual eyeballing.

This work is the subject of a separate session (see handoff prompt accompanying this spec).

---

## Open Questions / Future Work

### Content overlap signal

The verdict matrix's secondary signal (`contentClassesObserved.Count`) distinguishes KEEP from REVIEW within the high-frontier-growth bucket. The current audit log does not store `contentClassSig`, so this signal cannot be validated by the offline simulator using existing data.

Resolution: the audit log extensions in this design (`ContentBytes`, `LinksFound`, `ContentHash`, `PatternSigs`) will populate the missing fields going forward. After implementation, a second validation pass using fresh scrapes can confirm the secondary signal's effectiveness.

### Multi-root scrape (add_subtree)

Separately agreed: add a new MCP tool `add_subtree(library, version, url, ...)` that appends a subtree to an existing library/version index without `force=true` wipe semantics. Composable with this design: a first scrape with `autoExcludeWorthlessTemplates=true` discovers and excludes chrome; subsequent `add_subtree` calls add additional namespace roots into the same index, also benefiting from the auto-exclude logic.

Tracked as a separate design item; not part of this spec.

### Keyword dictionary as Tier-2 regex hint

The brainstorm discussion considered using a keyword dictionary ("login", "signin", "register", "account", ...) as a hint for regex generation. The final design uses purely structural longest-common-substring fallback because:

- Keyword dictionary is English-centric, fragile across i18n
- Statistical detection via probe-then-decide is already language-agnostic
- LCS fallback works on any URL space

Future work could add the dictionary back as a Tier-1.5 hint (between template-derived and LCS), but only if validation shows LCS-generated regexes are noticeably worse than dictionary-derived ones on real corpora.

### Search-result detection

`/search?q={value}` style templates land in the verdict matrix at "high frontier growth (if search surfaces unindexed pages) + content classes vary." Per the matrix, they get REVIEW. The LLM has to decide whether search-result pages are worth indexing.

A future refinement could detect "outbound links from search results overlap heavily with already-discovered URLs" as a stronger EXCLUDE signal for search specifically. Out of scope for v1.

### Cross-product pollution detection

A multi-product vendor site (Actipro WPF + Avalonia + WinForms + Universal) is currently handled by `RecommendedAllowedUrlPatterns` generation (longest common prefix of KEEP-verdict URLs). The analyzer could additionally emit a top-level `SiblingSubtreesDetected` field flagging the product-matrix shape explicitly, but this is a presentation refinement; the underlying allowlist recommendation already covers the case.

---

## Status and Next Steps

This design is **draft pending empirical validation**. Sequence:

1. Build the offline simulator in a fresh session (see handoff prompt)
2. Run simulator against the 21-library corpus, eyeball verdicts
3. Iterate algorithm details if needed; update this spec
4. When algorithm is validated, mark spec **Approved** and proceed to implementation plan (via `writing-plans` skill)
5. Implement: `TemplateExtractor`, `ProbeStateMachine`, audit log extensions, `scrape_docs` / `dryrun_scrape` parameter additions, `analyze_scrape_patterns` MCP tool, scrape-library skill recipe updates, test suite

The simulator work and the production implementation are separate; the simulator is throwaway.
