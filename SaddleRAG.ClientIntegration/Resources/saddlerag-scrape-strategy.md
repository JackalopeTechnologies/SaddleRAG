---
name: saddlerag-scrape-strategy
description: Decide HOW to scope a documentation scrape before running it. Use when facing an unfamiliar vendor docs site, when a previous scrape blew up the queue, or when the result was mostly noise. Covers the mental model (queue-at-insert vs pattern-at-pop), recon-first workflow, audit signals, generator fingerprints (Sandcastle, DocFX, bespoke), smell tests with concrete thresholds, and decision-tree recipes. Companion to `saddlerag:recon` (tactical) and `saddlerag:scrape` (mechanical).
---

# SaddleRAG scrape strategy

**Who this is for.** You're about to scrape a documentation site you don't fully understand. You need to decide *what* to scrape (scope), *what to exclude* (noise), and *how to size it* (cap) before running a single crawl. This skill teaches you to recognize the shape of the problem from a small recon, so the real scrape converges in 1–3 iterations instead of 5–10.

**What this is not.** This isn't the tactical "what arguments do I pass to `scrape_docs`" guide — that's `saddlerag:scrape`. And it isn't the step-by-step "how do I run a dryrun" — that's `saddlerag:recon`. This is the strategic *thinking* that sits above both.

---

## The mental model

A documentation scrape can go badly wrong in three ways:

1. **Discovery outruns processing.** The crawler finds links faster than it can fetch them, the queue grows into the millions, and useful pages get crowded out.
2. **The wrong content gets indexed.** Marketing pages, blog posts, navigation hubs, and per-property stubs flood the index. Search returns thin, low-value results.
3. **The right content doesn't get indexed.** The scrape stops at the marketing/nav layer and never reaches the substantive how-to or API pages buried under specific path prefixes.

All three fail the same way: **wrong pattern set**, not wrong knobs. `maxPages`, `fetchDelayMs`, and the URL parsing are downstream of the patterns. Get the patterns right and the rest takes care of itself.

### Why `maxPages` is not enough

The single most expensive lesson from the Infragistics work: **`maxPages` only caps the number of pages *fetched*. The discovery queue itself can grow unbounded.**

The crawler does this on every fetched page:
1. Fetch the page.
2. Extract every link (typically hundreds, sometimes thousands for nav-heavy pages).
3. Insert each link into the queue, subject to URL normalization + a dedupe check by exact URL string.
4. Pull the next URL from the queue.
5. **Now** evaluate it against `allowedUrlPatterns` / `excludedUrlPatterns`. Drop it if it doesn't match.
6. If it matches, fetch it. Repeat.

Step 3 has no pattern check. Step 5 does. So URLs that will eventually be excluded still get queued, classified by depth, and counted against the per-host queue ceiling (~100K URLs). When that ceiling hits, `QueueLimit` skip-reason starts firing and *useful* URLs get rejected because junk URLs are crowding them out.

The skill `saddlerag:scrape` calls this "pattern application is at pop time, not insert time." This skill explains why it matters strategically:

> **The job of `allowedUrlPatterns` is not just to filter, but to keep the queue from filling with garbage so dedupe + processing can keep up.**

Tight, prefix-anchored allowed patterns dramatically shrink the *discovered* URL set (because URLs not matching are dropped at pop time before they generate more links of their own). Loose patterns mean every fetched page contributes hundreds of garbage URLs to the queue, and the runaway is unstoppable.

---

## Recon-first protocol

Before any real scrape on an unfamiliar site, run this 5-minute recon:

1. **Pick a candidate root URL.** Often the doc landing page, e.g. `https://docs.example.com/wpf/index`.
2. **Run a 200-page dryrun** with a broad allowed pattern (just the doc subtree, like `^https://docs\.example\.com/wpf/`) and no excludes.
3. **Wait for `Status: Completed`**, not for `ItemsProcessed == ItemsTotal` (those lie during pipeline drain — see `saddlerag:scrape`'s "Dryrun limitations" if interested in why).
4. **Read the audit summary** via `inspect_scrape(jobId=...)`.
5. **Look at five signals** (next section).

If the recon shows the site is small (< 1,000 pages reachable) and clean (TotalConsidered ≈ FetchedCount × 2–5), skip everything else and go straight to `saddlerag:scrape` with sensible defaults.

If any smell test fires, design tighter patterns (using the fingerprints below) and run a second 200-page recon. Two recon iterations almost always converge.

---

## Reading the audit — five signals

From `inspect_scrape(jobId=...)` summary mode, look at:

### 1. The ratio `TotalConsidered ÷ FetchedCount`

| Ratio | Diagnosis |
|---|---|
| ≤ 50× | Clean site. Default patterns are probably fine. |
| 50× – 500× | Densely cross-linked. You need a tighter `allowedUrlPatterns` (per-section prefix), or you'll fight `QueueLimit`. |
| > 500× | Either URL-variant explosion (query string selectors, version dropdowns) or true nav-hub design. Find the multiplier and exclude it. |

Concrete from this session:

- **Infragistics WPF**, first scrape: 1M+ considered, ~10K fetched → 100×, runaway queue.
- **Actipro WPF**, first recon: 80K considered, 200 fetched → 400×. Driver was `?v=` query-string version selectors. After excluding `?v=`, recon dropped to 7K considered for 200 fetched → 35× → clean.

### 2. `SkipReasonCounts.QueueLimit`

If any value here is non-zero, the per-host queue hit its ceiling. **That's not a healthy crawl.** It means thousands of in-scope URLs were rejected before the crawler even looked at them. Tighten allowed patterns and recon again.

### 3. `SkipReasonCounts.PatternMissAllowed`

URLs that *were* evaluated and rejected because they didn't match `allowedUrlPatterns`. This is healthy in moderation (~1–10× FetchedCount) — it's the filter doing its job on off-host links and out-of-section in-host links. If it's much higher than that, your allowed pattern might be too loose (you're letting marketing/blog pages get extracted, link-checked, and rejected one at a time when you could exclude them en masse).

### 4. `SkipReasonCounts.PatternExclude`

URLs matching one of your `excludedUrlPatterns`. Should be the *primary* skip reason for known-junk URL families (tilde leaves, version selectors, year-versioned histories). If a junk family shows up as `PatternMissAllowed` instead of `PatternExclude`, your allow pattern is overshooting — tighten it.

### 5. `HostCounts`

If you see large counts on hosts you don't recognize (other vendors' sites, blog platforms, social), that's the off-host follow-up doing its work. Default `offSiteDepth=1` is usually fine. Set it to 0 if a vendor site links heavily to a tightly-coupled second domain you don't want indexed.

---

## Common generator fingerprints

Most vendor docs sites are generated by one of a handful of tools. Recognizing the generator tells you the URL patterns it produces and lets you skip 80% of the iteration.

### Sandcastle / SHFB (Sandcastle Help File Builder)

**Used by:** Infragistics, DevExpress, ComponentOne, Telerik, GrapeCity, most legacy .NET vendor docs.

**Fingerprints:**
- URLs contain `~` as a separator: `/help/<lib>/<Namespace>~<Namespace.Class>~<MemberName>`
- Per-class aggregator pages: `~ClassName_members`, `~ClassName_methods`, `~ClassName_properties`, `~ClassName_events`, `~ClassName_fields`
- Per-property leaf pages: `~ClassName~PropertyName` (two tildes) — these are almost always stubs (signature + one-liner + boilerplate)
- Sometimes also `T_<full.qualified.Type>.htm`, `M_<...>.htm`, `P_<...>.htm`, `E_<...>.htm`, `F_<...>.htm`, `N_<...>.htm` prefix-style URLs (older Sandcastle output)

**What to exclude:**
```
"~[^~/_][^~/]*~[^~/_][^~/]*$"                   # tilde-leaf property/method stubs
"_(members|methods|properties|events|fields)$"   # listing aggregators — INCLUDE these if you want API ref
```

**Strategic note:** *Include* the `_members` / `_methods` / etc. aggregators if you want API reference coverage. They contain the actual signature lists. The per-property pages (two-tilde leaves) are stubs and contribute almost zero value — verified by direct WebFetch on Infragistics property pages this session (signature + one-line description + boilerplate, nothing more).

### DocFX

**Used by:** Microsoft Learn portions, mathnet, ML.NET, Avalonia (some sections), most modern Microsoft-adjacent .NET projects.

**Fingerprints:**
- URLs end in `.htm` (not `.html`) — DocFX default
- API tree at `/<root>/api/<Namespace>/index.htm` (namespace index aggregators)
- Class pages at `/<root>/api/<Namespace>/<ClassName>.htm`
- No tilde separators in URLs — uses dots and slashes
- Often the home page does NOT link to `/api/` directly; you need `seedUrls` to reach the API tree

**What to do:**
- Pass both the docs root AND `/api/<Namespace>/index.htm` (or similar) as seeds. Single-rooted DocFX scrapes commonly miss the entire API tree.
- No tilde-leaf pattern needed; DocFX has no two-tilde leaf URLs.

### Bespoke / customized site (e.g. Actipro WPF)

**Used by:** Actipro, many modern vendors moving away from Sandcastle, doc-as-code shops.

**Fingerprints (variable, but watch for):**
- Hierarchical path-based URLs: `/<product>/<section>/<topic>`
- API tree often flattens namespace + class to URL path: `/api/<namespace.class>` (no separators between them)
- Version selectors as query strings: `?v=24.1`, `?v=23.1` — every page links to itself across all supported versions
- "Recent updates" / "Why choose us" marketing pages mixed into the docs tree

**What to exclude:**
```
"\\?v="                    # version-selector duplicates (5×+ URL multiplier)
"/recent-updates"          # marketing/changelog noise
"/why-choose-<vendor>"     # marketing
```

**Strategic note:** Version-selector URL duplication is the *invisible* killer of bespoke sites. A page with links to itself across `?v=21.1` through `?v=25.1` produces 5 unique URLs in the queue. Across a 4,000-page site, that's a 20,000-URL queue inflation. Add `\?v=` to `excludedUrlPatterns` whenever you see versioned URLs in the recon's fetched set.

### Sphinx (Python ecosystem — look for these)

**Used by:** Django, requests, pytest, NumPy, pandas, most major Python projects.

**Fingerprints (commonly observed):**
- `/_static/`, `/_modules/` paths
- `objects.inv` file in the root (intersphinx cross-reference index)
- Module pages: `/<module>.html` or `/<module>/<class>.html`
- "Edit on GitHub" links pointing to a specific commit

**Strategic note:** Sphinx sites are usually clean (low TotalConsidered ratios). Limit allowed pattern to the docs subtree, exclude `/_modules/` (source code rendering, not docs), and you're usually done.

### MkDocs (often with Material theme — look for these)

**Used by:** FastAPI, MkDocs itself, many CLI/tool docs.

**Fingerprints:**
- Heavy use of `/<topic>/index.html` paths
- Often versioned via `mike` plugin: `/<version>/<path>` like `/2.5.0/<topic>/index.html`
- Search-as-you-type usually means a `search_index.json` is in the static assets

**Strategic note:** MkDocs sites are *very* clean. The biggest trap is unversioned vs versioned URLs — pick one version (usually `/latest/`) and exclude the rest.

### Docusaurus (look for these)

**Used by:** React, Babel, Jest, much of the Meta open-source ecosystem.

**Fingerprints:**
- Versioned at `/docs/<x.x.x>/<path>` and `/docs/next/<path>`
- Blog at `/blog/`
- Markdown source visible in URLs as `.md` references

**Strategic note:** Default to one major version's docs only. Exclude `/blog/`, `/docs/next/`, and any version older than the one you're targeting.

---

## Smell tests with numeric thresholds

When reading a recon audit, treat these as red flags requiring action:

| Threshold | Signal | Action |
|---|---|---|
| `TotalConsidered ÷ FetchedCount > 50` | Densely cross-linked | Tighten allowed pattern to a per-section prefix |
| `QueueLimit > 0` | Queue ceiling hit | Tighten allowed pattern AND identify the URL multiplier |
| `PatternMissAllowed > 100 × FetchedCount` | Allow pattern is overshooting | Make allow pattern more specific OR add explicit excludes for the rejected families |
| `AlreadyVisited ÷ TotalConsidered > 0.3` | URL normalization isn't catching duplicates | Investigate query strings, trailing slashes, `?v=` variants |
| `Errors > 0.5% of FetchedCount` | Site is rate-limiting or has broken links | Increase `fetchDelayMs`, check for 5xx in errors, add server-specific rate-limit codes |
| `HostCounts` shows > 10 unique off-hosts | Pages are link-heavy with external refs | Lower `offSiteDepth` to 0 |
| Two seed URLs have *disjoint* fetched sets | Multi-hub site; one root won't reach all content | Use `seedUrls` to cover all hubs |

---

## Vendor pattern recipes

Copy-paste starting points by generator. Always run a 200-page recon first to confirm the fingerprint before committing.

### Sandcastle vendor docs (Infragistics-style)

```json
{
  "url": "<docs-hub-url>",
  "libraryId": "<vendor-product>",
  "version": "current",
  "fetchDelayMs": 3000,
  "additionalRateLimitStatusCodes": [502],
  "allowedUrlPatterns": [
    "^https://<host>/<docs-prefix>/(<product>|<sibling-product>|<NamespacePrefix>~)"
  ],
  "excludedUrlPatterns": [
    "~[^~/_][^~/]*~[^~/_][^~/]*$",
    "(20[01][0-9]|202[0-4])-volume-"
  ]
}
```

If you want API class listings, leave `_(members|methods|properties|events|fields)$` *out* of the excludes. If you want only conceptual content, add it.

### DocFX site (mathnet-style)

```json
{
  "url": "<docs-hub-url>",
  "libraryId": "<library>",
  "version": "current",
  "seedUrls": ["<docs-hub-url>/api/<MainNamespace>/index.htm"],
  "fetchDelayMs": 500,
  "allowedUrlPatterns": [
    "^https://<host>/<docs-prefix>/"
  ],
  "excludedUrlPatterns": []
}
```

The seed URL is the entire DocFX trick — without it, the home page often doesn't link to the API tree.

### Bespoke / version-selector site (Actipro-style)

```json
{
  "url": "<docs-hub-url>",
  "libraryId": "<vendor-product>",
  "version": "current",
  "seedUrls": ["<docs-hub-url>/api/index"],
  "fetchDelayMs": 500,
  "allowedUrlPatterns": [
    "^https://<host>/<docs-prefix>/"
  ],
  "excludedUrlPatterns": [
    "\\?v=",
    "/recent-updates",
    "/why-choose-<vendor>"
  ]
}
```

The `\?v=` exclude is mandatory if the site has a version dropdown that links the URL to itself.

### Sphinx / MkDocs (clean hierarchical)

```json
{
  "url": "<docs-hub-url>",
  "libraryId": "<library>",
  "version": "current",
  "fetchDelayMs": 500,
  "allowedUrlPatterns": [
    "^https://<host>/<docs-prefix>/"
  ],
  "excludedUrlPatterns": [
    "/_modules/",
    "/_sources/"
  ]
}
```

Usually nothing else needed.

---

## Decision tree

```
START
  │
  ├── Do you know the doc generator?
  │     │
  │     ├── Yes → use the matching recipe above, recon to confirm, scrape
  │     │
  │     └── No → continue to recon
  │
  ├── Run 200-page recon with broad allowed pattern, no excludes
  │
  ├── Read audit. Is TotalConsidered ÷ FetchedCount > 50?
  │     │
  │     ├── No → site is clean, scale up to real scrape
  │     │
  │     └── Yes → continue
  │
  ├── What's driving the multiplier?
  │     │
  │     ├── Tilde-style URLs → Sandcastle. Apply tilde-leaf exclude. Decide on _members.
  │     │
  │     ├── ?v= or similar query strings → bespoke version selector. Exclude \?v=.
  │     │
  │     ├── Nav-hub pages (>1000 links per fetched page) → tighten allow pattern to per-section prefix
  │     │
  │     ├── Off-site links dominate → lower offSiteDepth to 0
  │     │
  │     └── None of the above → check for AlreadyVisited explosion (URL canonicalization issue), or check error rate (rate limit)
  │
  ├── Re-run 200-page recon with the new patterns
  │
  ├── Recon now under 50× ratio?
  │     │
  │     ├── Yes → scale up maxPages, fire real scrape
  │     │
  │     └── No → repeat the "what's driving" analysis
  │
  └── Real scrape: maxPages = recon FetchedCount × 5 (rough), fetchDelayMs = 500–3000 depending on CDN sensitivity
```

---

## Scope-honest discipline

The most important strategic decision isn't a pattern — it's *what you're trying to index*. For a massive vendor docs site, the right scope is usually a subset, not the whole thing.

Examples from this session:

- **Infragistics WPF** — "index all of WPF" produced a 1M+ URL queue. "Index xamDataGrid + data-presenter family + ExcelEngine + Undo/Redo" produced a clean 3,375-page index. The narrow scope was both faster *and* more useful.
- **Actipro WPF** — small enough (~4K pages) that "everything in `/docs/controls/wpf/`" was tractable once the `?v=` driver was excluded.

Rule of thumb: if a 200-page recon shows TotalConsidered > 100K, the site is too big to "scrape everything." Pick the sections you care about and prefix-match those in `allowedUrlPatterns`.

---

## What this skill does NOT cover

- **The mechanics of `scrape_docs` parameters.** That's `saddlerag:scrape`.
- **The mechanics of `dryrun_scrape` and `inspect_scrape`.** That's `saddlerag:recon`.
- **What to do AFTER a scrape (querying, maintenance).** That's `saddlerag:query` and `saddlerag:maintain`.
- **An exhaustive catalog of every doc generator.** The long tail is too long; this skill teaches you to recognize *unknown* generators from the audit signals.
- **Estimating page count from scratch.** Use a 200-page recon, don't speculate.

---

## See also

- `saddlerag:recon` — tactical recon procedure (what commands to run)
- `saddlerag:scrape` — tactical scrape protocol (what parameters mean)
- `saddlerag:query` — how to query after a scrape lands
- `saddlerag:maintain` — diagnosing a broken index
- `case-study-infragistics-wpf.md` (in this directory) — the case study that informed this skill
