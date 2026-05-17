---
name: saddlerag-recon
description: Reconnaissance before scraping a documentation site into SaddleRAG. Use when you need to find the right root URL, understand a site's URL structure, build excludedUrlPatterns, or validate a scrape target before committing to a full crawl. Optional but saves iterations — if you already know the root URL and structure, go straight to saddlerag:scrape.
---

# SaddleRAG recon protocol

Recon is optional. If you already know the correct root URL and the site's URL structure is uniform (simple path hierarchy, no parameter noise), go straight to `saddlerag:scrape`. Do recon when you're unsure where to start, the site has complex URL patterns, or a previous scrape returned mostly noise.

## Goal

Find the exact root URL and any exclude patterns needed before calling `scrape_docs`. A wrong root URL wastes hours. A missing exclude pattern fills the index with thousands of useless stubs.

---

## Step 1 — Find the right root URL

The root URL is the crawl entry point. The crawler follows links outward from it, so everything reachable descends from it. Get this wrong and you either miss content or pull in marketing/blog noise.

**Rules for picking the root URL:**

- Start at the **documentation hub**, not the product homepage. Product homepages link to sales pages, blogs, and pricing — the crawler will follow all of them.
- The hub is typically a page titled "Documentation", "API Reference", "Guides", or "Developer Docs" that has a nav tree of actual content.
- If there's a sidebar or left-nav on the docs pages, find the URL of the page that *is* that nav — that's usually the right entry point.
- Prefer the deepest URL that still covers all the content you want. `https://docs.example.com/api/v3/` is better than `https://docs.example.com/` if you only want v3 content.

**Common wrong choices:**
| Wrong | Right |
|---|---|
| `https://www.infragistics.com/` | `https://www.infragistics.com/help/wpf/controls-components-and-frameworks` |
| `https://docs.mongodb.com/` | `https://www.mongodb.com/docs/drivers/csharp/current/` |
| `https://reactjs.org/` | `https://react.dev/reference/react` |

---

## Step 2 — Inspect the URL structure

Before writing patterns, understand what URLs the site produces. Fetch the root URL and scan the links on it. Look for:

- **Path structure** — are docs under `/guide/`, `/api/`, `/reference/`? Does the path encode version numbers?
- **URL parameters** — some sites use `?tab=csharp` or `#section` fragments. Parameters usually don't produce distinct pages and add noise.
- **Special separators** — some vendors (Infragistics, DevExpress) use `~` or `_` as separators within path segments to encode assembly/namespace/class/member hierarchies. These produce thousands of leaf pages that are thin property stubs, not useful documentation.
- **Off-host links** — does the page link to CDNs, GitHub, or marketing subdomains? Those will be followed up to `OffSiteDepth` (default: 1 hop).

**The tilde pattern (Infragistics, DevExpress, similar vendors):**

URLs like:
```
/help/wpf/InfragisticsWPF.DataPresenter~Infragistics.Windows.DataPresenter.DataPresenterBase_members
/help/wpf/InfragisticsWPF.DataPresenter~Infragistics.Windows.DataPresenter.DataPresenterBase~Columns
```

The first (`_members` suffix) is the class-level member listing — useful. The second (bare `~PropertyName` terminal) is a single-property stub — not useful, causes 502 rate-limit bursts, and clogs classification.

Pattern to exclude property/method stubs while keeping class-level pages:
```
~[^~/_][^~/]*~[^~/_][^~/]*$
```
This matches any URL whose last path component contains two `~`-delimited segments, which is the signature of a leaf stub.

---

## Step 3 — Run a dry-run scrape

`dryrun_scrape` crawls the site without ingesting anything. It tells you what pages would be fetched, what would be excluded, and what depth they appear at — without touching your index.

```
dryrun_scrape(
  url="<root url>",
  libraryId="<your-id>",
  version="current",
  hint="<what this library is>",
  excludedUrlPatterns=["<pattern>"],   // add patterns you're testing
  maxPages=200                          // cap it for recon — you don't need all 10k
)
```

Then call `get_scrape_status(jobId=...)` and watch it run. When complete, call `inspect_scrape(jobId=...)` to read the audit log.

---

## Step 4 — Read the audit log

`inspect_scrape` returns a breakdown of every URL the dry run touched: fetched, skipped (with reason), excluded (with matching pattern), and errored.

What to look for:

- **PatternExclude** entries — your pattern is matching. Check samples to make sure it's excluding the right things.
- **DepthExceeded** — content you want is being cut off. Consider increasing `inScopeDepth`.
- **HTTP 4xx/5xx errors** — which status codes? 502 in bursts = rate limiting. 403 = WAF block. 404 = broken links (normal, ignore).
- **Page counts by depth** — if depth 3 has 50 pages and depth 4 has 5,000, there's an explosion at that level. That's where to add an exclude pattern.
- **Domain distribution** — are off-site hops pulling in CDNs or GitHub repos you don't want?

Iterate: refine patterns → re-run dryrun → re-inspect → repeat until the page count and composition look right.

---

## Step 5 — Build your `excludedUrlPatterns`

Once you understand the URL structure, write patterns. These are case-insensitive regexes matched against the full URL.

| Goal | Pattern |
|---|---|
| Exclude property/method leaf stubs (tilde vendors) | `~[^~/_][^~/]*~[^~/_][^~/]*$` |
| Exclude a specific path prefix | `/blog/` |
| Exclude URLs with query parameters | `\?` |
| Exclude fragment-only anchors | `#` |
| Exclude a specific file extension | `\.pdf$` |
| Exclude off-site CDN | `cdn\.example\.com` |

Test patterns with the dry-run before committing.

---

## When you're ready

Hand off to `saddlerag:scrape` with:
- The validated root URL
- Your `excludedUrlPatterns` list
- Any `additionalRateLimitStatusCodes` you identified from the dry-run error log
- A `fetchDelayMs` estimate based on observed error rate
