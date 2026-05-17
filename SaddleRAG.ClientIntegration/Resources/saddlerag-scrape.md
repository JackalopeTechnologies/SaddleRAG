---
name: saddlerag-scrape
description: Execute a documentation scrape into SaddleRAG effectively. Use when calling scrape_docs to ingest a library — covers parameter selection, rate-limit tuning, failure diagnosis, monitoring, and cancel/resume. If you're uncertain about the target site's URL structure or root URL, saddlerag:recon can help before you commit.
---

# SaddleRAG scrape protocol

This skill covers how to run `scrape_docs` effectively. If you're uncertain about the right root URL or need to build exclude patterns, `saddlerag:recon` can help first — but it's optional. If you already know the target, start here.

---

## The scrape_docs parameters that matter most

| Parameter | What it actually does | Guidance |
|---|---|---|
| `url` | Crawl entry point | Must be the docs hub, not the product homepage |
| `libraryId` | Storage key | Use a stable slug: `react`, `infragistics-wpf`, `mongodb-csharp` |
| `version` | Storage key | `current` unless you need version-pinned docs |
| `hint` | Seeds the LLM classifier | Be specific: `"Infragistics WPF controls — how-to guides and API reference"` |
| `fetchDelayMs` | Floor delay per worker | Not a global rate cap — see concurrency note below |
| `excludedUrlPatterns` | Regex list against full URL | Applied before fetch; blocks both the page and its children |
| `additionalRateLimitStatusCodes` | Extra HTTP codes to treat as rate-limit signals, added on top of the built-in defaults (429, 503) | Omit for most sites; add [502] for Infragistics-style CDNs, [520, 521, 522] for Cloudflare |
| `maxPages` | Safety cap | Set to a few hundred for a first run on an unknown site |
| `force` | Re-scrape even if cached | Use when source docs have changed |
| `resume` | Restart from where a cancelled job left off | Reuses stored job config; `url` is optional |

---

## Concurrency — the most misunderstood lever

The crawler runs **8 parallel workers**. Each worker waits `fetchDelayMs` between its own fetches. `fetchDelayMs` does **not** serialize workers — at 500 ms with 8 workers, you can have 8 requests in flight simultaneously.

The per-host AIMD rate limiter starts at 4 concurrent fetches and grows/shrinks automatically based on response signals — but only for the status codes in `additionalRateLimitStatusCodes` plus the built-in defaults (429, 503). If a site returns 502 as a soft rate limit and 502 is not in that list, the limiter never backs off.

**Rule of thumb for sensitive sites:** set `fetchDelayMs=3000`. That's the main lever you have without a source change. It won't fully serialize fetches but reduces effective throughput to ~2–3 req/s across all workers, which is gentle enough for most CDNs.

---

## Choosing `additionalRateLimitStatusCodes`

The built-in defaults (429, 503) always apply. This parameter adds site-specific codes on top — it does not replace the defaults.

| Symptom in audit log | Likely cause | Fix |
|---|---|---|
| Bursts of 502 errors | CDN using 502 as a soft rate limit (Infragistics, similar) | `additionalRateLimitStatusCodes=[502]` |
| 520/521/522 errors | Cloudflare rate wall | `additionalRateLimitStatusCodes=[520, 521, 522]` |
| Bursts of 503 errors | Origin overloaded | Already in built-in defaults — no action needed |
| Lots of 403 errors | WAF bot detection | 403 is not a rate signal — it triggers scope filtering, not throttling |

---

## Common failure signatures

### 502 bursts on leaf pages
**Symptom:** Audit log shows many 502s on URLs with a repeated structural pattern (e.g. two `~` segments, deep numeric IDs).  
**Cause:** Fetchers hit densely-linked thin pages in a burst before AIMD can react — and the CDN is using 502 as a soft rate-limit signal rather than a genuine gateway error.  
**Fix:** Add the pattern to `excludedUrlPatterns` to prevent those pages being queued at all. Also set `additionalRateLimitStatusCodes=[502]` so the AIMD limiter backs off if any 502s do arrive.

### Classification log spam every ~100 seconds
**Symptom:** Logs show repeated `LLM classification failed for <url>` warnings on the same type of URL.  
**Cause:** The classifier is timing out on stub pages with almost no content. One timeout stalls the entire pipeline (classification is serial).  
**Fix:** Exclude those URL patterns so they never reach the classify stage.

### Crawl appears to stall (no progress for minutes)
**Symptom:** `get_scrape_status` shows pages fetched but classified count is far behind and not moving.  
**Cause:** Classification backpressure. The classify stage is serial and has a 50-page buffer. If it's blocked on a slow Ollama call, all 8 crawl workers eventually stall waiting for the buffer to drain.  
**Fix:** If the stalled URL type is low-value, add it to `excludedUrlPatterns` and cancel+restart.

### Index fills with useless short pages
**Symptom:** After scrape, `search_docs` returns thin property stubs instead of guides or class docs.  
**Cause:** The site has leaf pages (individual property/method stubs) that are sparsely linked from class-level pages and got indexed.  
**Fix:** Use `excludedUrlPatterns` to block the structural pattern of those pages. Re-scrape with `force=true`.

---

## Monitoring a running scrape

```
get_scrape_status(jobId="<id>")        // overall status + page counts
inspect_scrape(jobId="<id>")           // URL-by-URL audit: what was fetched, excluded, errored
```

Watch for:
- `PagesClassified` falling far behind `PagesFetched` — classification bottleneck
- High error rate in the audit log — rate limiting or WAF
- Unexpected domains appearing — off-site link following

---

## Cancel and resume

If you catch a problem mid-scrape:

```
cancel_scrape(jobId="<id>")
```

Partial results are kept. Fix your patterns, then either:
- `scrape_docs(libraryId=..., version=..., resume=true)` — restart from stored state, reusing the previous job's URL and patterns (you can override individual params)
- `scrape_docs(url=..., ..., force=true)` — start clean (clears partial data first)

---

## Worked examples

**Infragistics WPF** (CDN returns 502 as a soft rate limit):
```json
{
  "url": "https://www.infragistics.com/help/wpf/controls-components-and-frameworks",
  "libraryId": "infragistics-wpf",
  "version": "current",
  "hint": "Infragistics WPF controls documentation — how-to guides, overviews, and API class/member listings",
  "fetchDelayMs": 3000,
  "excludedUrlPatterns": ["~[^~/_][^~/]*~[^~/_][^~/]*$"],
  "additionalRateLimitStatusCodes": [502]
}
```

**Cloudflare-fronted sites** (520–522 are Cloudflare-specific rate/origin-error codes):
```json
{
  "url": "https://docs.example.com/",
  "libraryId": "example-lib",
  "version": "current",
  "hint": "...",
  "additionalRateLimitStatusCodes": [520, 521, 522]
}
```

- Root URL is the controls hub, not the product homepage
- `fetchDelayMs=3000` throttles workers on a CDN-sensitive site
- The tilde pattern excludes `~ClassName~PropertyName` leaf stubs while keeping `~ClassName_members` class listings
- `additionalRateLimitStatusCodes` extends the defaults (429, 503) — it does not replace them
