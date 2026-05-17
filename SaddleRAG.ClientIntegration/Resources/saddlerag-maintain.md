---
name: saddlerag-maintain
description: Maintain, diagnose, and repair a SaddleRAG library index. Use when a library is suspect, search results are thin or wrong, a scrape job is stale or failed, or you need to decide which pipeline stage to re-run. Also covers the dashboard as a session-start health check.
---

# SaddleRAG maintenance protocol

## Start here every session: the dashboard

Before doing anything else in a fresh session, call:

```
get_dashboard_index()
```

It returns:
- **Library and version counts** — what's indexed
- **Recent jobs** — running, completed, failed, stale
- **Suspect libraries** — libraries flagged for URL or quality problems
- **SuggestedNextAction** — always act on this first

A job is **stale** when `recentJobs[].Stale = true` (Running but no progress for 4+ hours). Cancel it:
```
cancel_scrape(jobId="<id>")
```

A library is **suspect** when its URL has been flagged as wrong. Do not re-scrape — fix the URL first:
```
submit_url_correction(libraryId=..., version=..., newUrl="<corrected url>")
```
That clears the flag and re-queues with the corrected URL in one call.

---

## The pipeline — what each stage does

Every page goes through five stages in order. Each stage can be re-run independently:

```
crawl → classify → chunk → embed → index
```

| Stage | What it produces | Re-run tool |
|---|---|---|
| **crawl** | Raw HTML fetched from source | `rescrape_library` or `scrape_docs` |
| **classify** | `DocCategory` tag on each page | `reextract_library` (re-classifies + re-extracts) |
| **chunk** | Text chunks split from page content | `rechunk_library` |
| **embed** | Vector embeddings per chunk | `reembed_library` |
| **index** | BM25 + vector index updated | Happens automatically after embed |

**Only re-run the stage that broke.** Re-running an earlier stage cascades through all later stages, which is slow and unnecessary if the problem is downstream.

---

## Diagnosing a bad index

Call `get_library_health(libraryId=..., version=...)` first. It reports:
- Chunk count and page count
- Category distribution (how many pages are HowTo vs ApiReference vs Unclassified)
- Embedding coverage (pages with vs without embeddings)
- Any boundary or extraction issues

**What the numbers mean:**

| Symptom | Likely cause | Fix |
|---|---|---|
| Chunk count near zero | Crawl didn't fetch real content (wrong root URL, WAF block, JS-rendered pages) | Recon + re-scrape |
| High Unclassified % | LLM classifier timed out during scrape (happens on stub-heavy sites) | `reextract_library` |
| Correct page count but bad search results | Wrong pages indexed (noise pages, navigation chrome, marketing) | Recon + re-scrape with better excludedUrlPatterns |
| Missing embedding coverage | Embed stage failed or was interrupted | `reembed_library` |
| Correct chunk count, correct categories, bad results | Chunking strategy mismatch for this content type | `rechunk_library` |

---

## Decision tree: which tool to use

```
Is the source site content out of date?
  → Yes: rescrape_library (re-crawls from stored page URLs, no config needed)
  → No, continue:

Are the wrong pages indexed (noise, stubs, navigation)?
  → Yes: fix excludedUrlPatterns → scrape_docs(force=true)
  → No, continue:

Is the Unclassified % high (>20%)?
  → Yes: reextract_library (re-runs classify + extract on existing fetched pages)
  → No, continue:

Is embedding coverage incomplete?
  → Yes: reembed_library
  → No, continue:

Are chunks too large / too small / splitting in wrong places?
  → Yes: rechunk_library
  → No: the index is probably fine — the problem may be in how you're querying (see saddlerag:query)
```

---

## Key tool reference

| Tool | What it does | When to use |
|---|---|---|
| `rescrape_library` | Re-crawls from stored page URLs; link-following picks up new pages | Routine refresh when source docs have changed |
| `scrape_docs(force=true)` | Clears existing data, re-crawls from root URL with full config | Wrong root URL or wrong crawl patterns |
| `reextract_library` | Re-runs classify + content extraction on already-fetched pages | High Unclassified %, bad category tags, extractor updated |
| `rechunk_library` | Re-chunks existing extracted content | Chunk size/strategy mismatch |
| `reembed_library` | Re-embeds existing chunks (e.g. after switching embedding model) | Embedding model changed, coverage gap |
| `get_library_health` | Diagnostic report: counts, categories, coverage | First step in any diagnosis |
| `inspect_scrape` | URL-by-URL audit log from a scrape job | Understanding what was fetched, excluded, or errored |
| `submit_url_correction` | Clears suspect flag, corrects URL, re-queues | Suspect library with wrong root URL |
| `delete_version` | Removes all data for a (library, version) | Starting completely fresh |
| `cancel_scrape` | Stops a running or stale job | Stale job, wrong config caught mid-run |

---

## The Unclassified problem

During scraping, the LLM classifier runs on every page. If it times out (which happens on stub-heavy sites with thousands of thin pages), the page is stored as `Unclassified` and passes through. This is silent — no error, the page just has no category tag.

Consequences:
- `search_docs(category="HowTo")` will miss content that should be HowTo but got tagged Unclassified
- The proportion of Unclassified pages in `get_library_health` tells you how severe this was

Fix: `reextract_library` re-runs classification on the already-fetched pages without re-crawling. Much faster than a full re-scrape.

Prevention: exclude stub pages with `excludedUrlPatterns` before they enter the classify stage.

---

## Profiles

Profiles are isolated namespaces within a single SaddleRAG instance. Same library ID can exist in two profiles with different content.

Use profiles when:
- You want to test a new scrape without affecting the active index
- You need separate indexes for different projects or teams

Pass `profile="name"` to all tools. `list_profiles()` shows what exists.
