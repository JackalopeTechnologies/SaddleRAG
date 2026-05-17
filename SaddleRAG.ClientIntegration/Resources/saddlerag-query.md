---
name: saddlerag-query
description: Query a SaddleRAG index effectively. Use when searching for documentation, looking up a class or symbol, orienting on an unfamiliar library, or diagnosing why search results are thin. Covers tool selection, category filtering, chaining strategies, and what to do when results disappoint.
---

# SaddleRAG query protocol

## The four query tools and when to use each

| Tool | Best for | When NOT to use |
|---|---|---|
| `search_docs` | Natural-language questions, concept search, finding guides | Exact symbol lookup — use get_class_reference instead |
| `get_class_reference` | Looking up a specific class, type, interface, or enum by name | Broad conceptual questions |
| `get_library_overview` | First orientation on an unfamiliar library | When you already know what you're looking for |
| `list_symbols` | Exploring what's documented when you don't know the symbol name | Anything requiring content — returns names only |

Start with `list_libraries` in any fresh session to see what's indexed before querying.

---

## Effective `search_docs` usage

### Always scope to a library when you know it
```
search_docs(query="how to bind a command to a button", library="infragistics-wpf")
```
Unscoped search hits all indexed libraries and dilutes results with irrelevant content.

### Use category to narrow — but know its limits
The `category` parameter filters by the page's classified category:

| Category | What's there |
|---|---|
| `Overview` | Concepts, architecture, getting-started |
| `HowTo` | Tutorials, guides, step-by-step walkthroughs |
| `Sample` | Code examples and demos |
| `ApiReference` | Class/method/property documentation |
| `ChangeLog` | Release notes, migration guides |

**Caveat:** classification can fail silently on poorly-scraped libraries. Pages that timed out during classification are stored as `Unclassified` and won't appear in category-filtered results. If a category-scoped search returns nothing, try the same query without `category` — the content may exist but be unclassified. If you find it that way, the fix is `reextract_library` (see `saddlerag:maintain`).

### Phrase for the content, not the question
Good: `"DataPresenterBase column binding WPF"`  
Less good: `"how do I bind columns in DataPresenterBase?"`

The index stores documentation text, not Q&A pairs. Match the vocabulary the docs would use.

### Increase `maxResults` when the first few hits miss
Default is 5. For broad topics where you're not sure which chunk has what you need, try 10–15.

---

## Effective `get_class_reference` usage

Tries an exact match first, then fuzzy. You don't need the full namespace:

```
get_class_reference(className="DataPresenterBase", library="infragistics-wpf")
get_class_reference(className="FieldLayoutSettings")   // searches all libraries
```

If the class isn't found but you're confident it should be indexed:
1. Try `list_symbols(library=..., filter="DataPresenter")` to see what's actually documented
2. If it's not there, the class may not have been scraped (check the scrape depth and exclude patterns)

---

## Chaining: the right sequence

For an unfamiliar library:
1. `get_library_overview` — understand the architecture and key concepts
2. `list_symbols(library=..., kind="class")` — see what types are documented
3. `get_class_reference` — drill into the specific type you need
4. `search_docs(category="HowTo")` — find guides for the workflow you're implementing
5. `search_docs(category="Sample")` — find code examples

For a specific "how do I X" question:
1. `search_docs(query="X", library=..., category="HowTo")` — guides first
2. If thin results: `search_docs(query="X", library=...)` — drop category filter
3. `get_class_reference` for the specific API involved
4. `search_docs(category="Sample")` for code

For a symbol lookup:
1. `get_class_reference(className=..., library=...)` — fastest path
2. Fall back to `search_docs` if fuzzy match fails

---

## When results are thin or wrong

Work through this before concluding the content doesn't exist:

**1. Drop the category filter**  
Try without `category=` — content may exist but be Unclassified.

**2. Broaden the query**  
Try synonyms or the parent concept. `"column layout"` instead of `"FieldLayout configuration"`.

**3. Check what's actually indexed**  
`list_symbols(library=...)` — are the expected types there?  
`get_library_health(libraryId=..., version=...)` — how many pages/chunks? What's the Unclassified %?

**4. Check if the right pages were scraped**  
`inspect_scrape(jobId=...)` from the most recent scrape job — were the relevant URLs fetched, excluded, or errored?

**5. If the index looks good but results are still wrong**  
The scrape may have captured the right URLs but extracted the wrong content (navigation chrome, boilerplate, JS-rendered content the fetcher couldn't parse). Check `list_pages(libraryId=..., version=...)` and spot-check a few URLs to see what was actually stored.

**If none of the above resolves it:** the content genuinely may not be in the docs, or may require a re-scrape with a better root URL. See `saddlerag:recon`.

---

## Query quality is downstream of index quality

This is the most important thing to understand: if the scrape was noisy or incomplete, the best query won't rescue it. Symptom patterns:

| What you observe | What it usually means |
|---|---|
| Results are all navigation/boilerplate text | Wrong pages indexed — re-scrape needed |
| Correct library, totally wrong topic | Embedding model saw a lot of off-topic pages |
| Results exist but are surface-level stubs | Property-stub pages weren't excluded during scrape |
| Nothing returned even for obvious queries | Low chunk count — crawl may have been blocked or misconfigured |

In all these cases, the fix is upstream in the scrape, not in the query. See `saddlerag:maintain` for diagnosis tools, `saddlerag:recon` and `saddlerag:scrape` for re-ingestion.
