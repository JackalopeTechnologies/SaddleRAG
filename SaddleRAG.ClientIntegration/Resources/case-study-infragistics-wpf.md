# Scraping lessons from Infragistics WPF

Field notes from indexing a focused subset of the Infragistics WPF docs
into SaddleRAG, and what we'd carry forward to any large vendor doc
site. This is the source material for the future
`saddlerag:scrape-strategy` skill — written while the experience is
fresh so the lessons are concrete, not abstract.

## The target

`https://www.infragistics.com/help/wpf/...` — a sprawling Sandcastle-style
vendor docs site with:

- A homepage controls hub linking to ~150 controls
- Per-control how-to topics (`xamdatagrid-grouping`, `xamdata-supplying-data`, …)
- An API tree under `InfragisticsWPF.<assembly>~...` namespaces with single-
  and double-tilde class/member paths
- Year-versioned release-notes histories going back to 2007 (`whats-new-in-2010-volume-2`,
  `breaking-changes-in-2015-volume-1`, `wpf-2008-volume-1`)
- Heavy marketing/account/product crosslinks on every help page

Stated goal: index a focused subset (xamDataGrid, xamDataPresenter family,
ExcelEngine, Undo/Redo Framework) deeply enough to answer real "how do I"
questions. Not the whole site.

## What went wrong (and how we fixed it)

### Attempt 1 — root URL only, default settings
Queue grew to **1,010,000+** URLs in ~2.5 hours. The crawler hadn't even
processed 10,000 of them. Discovery was happening 100× faster than
processing.

**Diagnosis:** every fetched page extracted ~hundreds of links, all of
which got queued for evaluation. Pattern checks happen at pop time, not
at queue insertion. Without an aggressive `allowedUrlPatterns`, queue
grows unbounded.

**First wrong instinct:** "just raise `maxPages` and let it run." Wrong
— `maxPages` only caps **fetched** count. The queue itself keeps growing
either way.

### Attempt 2 — added tilde-leaf exclude + class-listing exclude
We added:
```
"~[^~/_][^~/]*~[^~/_][^~/]*$"        # ~Class~Property leaves
"_(members|methods|properties|events|fields)$"   # _members listings
"whats-new-in-(201[2-9]|202[0-4])"   # legacy whats-new
```
Queue still reached **1.4M** in an hour. The excludes were working
(150K pattern-excluded), but discovery still outran processing.

**Diagnosis:** the excludes filter URLs at pop time. They were doing real
work. But the host-wide `allowedUrlPatterns` (default = the whole host)
let in marketing, account, product, blog, and forum URLs from every page
footer. Each of those then had its own link-rich page.

### Attempt 3 — tighter allowedUrlPatterns to `/help/wpf/` only
Queue peaked at **137K** — bounded but still huge. The crawler was now
staying inside the docs subtree but still spraying across 150 controls
because every control's overview page links to every other control.

**Diagnosis:** "stay inside `/help/wpf/`" isn't tight enough when the
target site has 150 cross-linking controls. Need per-control prefixes.

### Attempt 4 — per-control prefix allow + multi-seed
Final allow pattern:
```
^https://www\.infragistics\.com/help/wpf/(
  excelengine|undo-redo-framework|xamdatagrid|xamdatapresenter|
  xamdata-|wpf-the-data-presenter-family|wpf-about-the-data-presenter-family|
  differences-between-xamgrid-and-xamdatagrid|implementing-undo-redo|
  InfragisticsWPF\.datapresenter~|InfragisticsWPF\.editors~|
  InfragisticsWPF\.documents\.excel~
)
```
Plus `seedUrls` to anchor at multiple control roots so we didn't depend
on the navigation hub being linked from every starting point.

Result: 3,375 pages, 15,877 chunks, 0 errors, ~52 minutes. Queue
drained naturally to 0 — didn't hit the 5000-page safety cap.

### Surprise — `xamdata-` prefix carries half the substance
xamDataGrid's documentation lives under **two prefixes**:
- `xamdatagrid-*` (control-specific topics: `xamdatagrid-grouping`)
- `xamdata-*` (data-presenter family shared topics: `xamdata-supplying-data`)

We initially built the allow pattern around `xamdatagrid` only and got
178 pages instead of 3,375. The missing 95% of useful content was
under `xamdata-*`. Probing the dryrun audit's depth-1 fetched URLs
caught this — without recon we'd have shipped a useless index.

### Surprise — per-property pages are stubs
Per-property doc pages (`InfragisticsWPF.x~Class~Property`) are pure
stubs: name + signature + one-line description + boilerplate target
platforms. The same content (in aggregated form) appears on the class's
`_members` listing page.

Verdict: exclude per-property pages, include `_members`/`_methods`/etc.
listings. Verified by WebFetch on three sample property pages.

### Surprise — year-versioned legacy hides in three URL shapes
We initially excluded `whats-new-in-2010-volume-2` but missed
`breaking-changes-in-2010-volume-2` and `wpf-2008-volume-1`. Replaced
two specific patterns with one general one:
```
(20[01][0-9]|202[0-4])-volume-
```
Catches all three URL families uniformly.

### Storage hygiene afterward
After 5+ create/delete-library cycles, MongoDB had accumulated **1.6 GB
of file-level free space** that WiredTiger reuses on insert but never
returns to the OS. Running `compact` per collection reclaimed the
space — biggest win was `scrape_audit_log` (1.3 GB → 167 MB). This led
to PR #90 wrapping `compact` as an MCP tool.

## Lessons that generalize

**1. URL shape is the dominant lever.** Once you understand the site's
URL conventions, the right `allowedUrlPatterns` and `excludedUrlPatterns`
matter 100× more than `maxPages` or `fetchDelayMs`. Spend the time on
the patterns first.

**2. `maxPages` is a safety cap, not a strategy.** It limits *fetched*
pages; the queue itself can still grow into the millions if
`allowedUrlPatterns` is loose. A scrape that hits `maxPages` with
hundreds of thousands of URLs still queued is a sign the patterns are
wrong, not that the cap is too low.

**3. Pattern application is at pop time, not insert time.** Discovered
links land in the queue regardless of patterns. They're only evaluated
when popped. This means a permissive `allowedUrlPatterns` lets the
queue bloat even if everything would eventually be filtered.

**4. Vendor docs sites have three filter tiers.** Use all three:
   - `allowedUrlPatterns`: tight regex around the actual content prefixes
   - `excludedUrlPatterns`: tilde-leaves + per-vendor listing stubs + legacy histories
   - `maxPages`: backstop only

**5. Multi-seed beats single-root for vendor sites.** Starting from one
control's hub page makes the crawl depend on that one page linking to
every section you care about. Seed with every control root explicitly
(via `seedUrls`) so a single broken or moved nav link can't make a
whole section invisible.

**6. Nav-hub pages spray crawl.** Any page that lists "every control in
the suite" will, by default, drag in the whole suite. If you're scoped
to a subset, your `allowedUrlPatterns` MUST prefix-match only the
controls you care about, not the whole `/help/wpf/` tree.

**7. Recon dryruns are cheap; long scrapes are expensive.** A 200-page
`dryrun_scrape` finishes in 1–3 minutes and tells you whether your
patterns are sane. A 30-minute real scrape that needs to be cancelled
costs a real-scrape's worth of CPU + Mongo churn. Always recon first
on a new vendor.

**8. Trust the audit, not the counter.** `ItemsProcessed`/`ItemsTotal`
on a `dryrun_scrape` job lie during the pipeline-drain phase
(post-fetch but pre-completion). Use `inspect_scrape`'s
`FetchedCount` instead, and wait for `Status: Completed` before
trusting any total.

**9. Iteration is the norm.** Plan for 3-5 dryrun iterations on a new
vendor before the patterns are right. Every iteration teaches you a
new URL shape you didn't anticipate.

**10. Scope-honest beats completeness.** "Index all of Infragistics
WPF" was the wrong goal. "Index xamDataGrid + data-presenter family +
ExcelEngine + Undo/Redo, deep enough to answer real questions" was the
right one. Picking the right scope is half the work.

**11. After heavy churn, run `compact`.** WiredTiger reuses freed
space within a collection file but never returns it to the OS. A few
create/delete-library cycles on a large library accumulate hundreds of
megabytes of dead space. `compact_collections` (PR #90) is the
maintenance tool.

## Smell tests for the next site

Before committing to a real scrape on a new vendor, dryrun against a
single root for 200 pages and read the audit. Red flags:

| Signal | Meaning |
|---|---|
| `TotalConsidered` > 50× `FetchedCount` | Densely crosslinked; needs per-section allow patterns |
| `PatternMissAllowed` dominates skips | Default host scope is too broad; tighten allow |
| `QueueLimit` shows up in `SkipReasonCounts` | Queue saturated; allow patterns way too loose |
| Two same-host URL prefixes that look unrelated link to each other | Nav-hub site; you'll be tempted to use one as root |
| URL paths contain `~` or repeated `_` separators | Sandcastle/DocFX vendor; expect tilde-leaf stubs |
| URLs with years (`-YYYY-volume-`, `whats-new-in-YYYY`) | Legacy histories; you almost never want them |

## What we'd do differently next time

1. **Always start with a 200-page dryrun** on a broad `/host/path/` scope
   and read the audit. Don't open with a real scrape.
2. **Probe the fetched URLs at depth 1**, not just depth 0. Depth 0 is
   the entry page — depth 1 is what the crawler actually does in
   practice.
3. **Check for nav-hub vs scoped behavior** before settling on a root.
   If depth-1 fetched URLs span dozens of unrelated topics, it's a
   nav-hub and you need a tighter allow pattern.
4. **Spot-check `_members` and per-property pages** with `WebFetch`
   *before* indexing them. Confirming they're stubs (or aren't) takes
   30 seconds and saves a full re-scrape.
5. **Plan exclusion patterns for legacy histories** up front. Search
   for any `-YYYY-volume-` or `whats-new-in-YYYY-` family and exclude
   the year range you don't care about with a single pattern.
6. **Compact after every major churn.** Cancelled and re-fired scrapes
   leave significant disk-level garbage even after `cleanup_orphans`.
