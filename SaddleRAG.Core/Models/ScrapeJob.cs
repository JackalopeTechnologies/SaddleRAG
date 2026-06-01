// ScrapeJob.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Configuration for a documentation library scrape operation.
/// </summary>
public record ScrapeJob
{
    /// <summary>
    ///     Root URL to begin crawling from.
    /// </summary>
    public required string RootUrl { get; init; }

    /// <summary>
    ///     Human-readable hint about what this library is.
    ///     Used to seed the classifier.
    /// </summary>
    public required string LibraryHint { get; init; }

    /// <summary>
    ///     Unique identifier for this library in storage.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version string for this scrape.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     URL patterns to stay within during crawl.
    /// </summary>
    public required IReadOnlyList<string> AllowedUrlPatterns { get; init; }

    /// <summary>
    ///     URL patterns to explicitly exclude.
    /// </summary>
    public IReadOnlyList<string> ExcludedUrlPatterns { get; init; } = [];

    /// <summary>
    ///     URL pattern hints for classification.
    ///     Maps regex patterns to document categories.
    /// </summary>
    public IReadOnlyDictionary<string, DocCategory> UrlClassificationHints { get; init; } =
        new Dictionary<string, DocCategory>();

    /// <summary>
    ///     Authentication configuration for sites requiring login.
    ///     Null means no authentication needed.
    /// </summary>
    public ScrapeAuthentication? Authentication { get; init; }

    /// <summary>
    ///     Content extraction strategy override.
    ///     Null means use auto-detection.
    /// </summary>
    public string? ContentExtractorId { get; init; }

    /// <summary>
    ///     Delay in milliseconds between page fetches.
    /// </summary>
    public int FetchDelayMs { get; init; } = DefaultFetchDelayMs;

    /// <summary>
    ///     Maximum number of pages to crawl. Safety limit. 0 = no limit.
    /// </summary>
    public int MaxPages { get; init; }

    /// <summary>
    ///     Maximum link-following depth for pages WITHIN the root scope
    ///     (same host and path prefix). 0 = no limit.
    /// </summary>
    public int InScopeDepth { get; init; } = 10;

    /// <summary>
    ///     Maximum link-following depth for pages on the SAME HOST
    ///     but outside the root path prefix.
    /// </summary>
    public int SameHostDepth { get; init; } = 5;

    /// <summary>
    ///     Maximum link-following depth for pages on a DIFFERENT HOST entirely.
    ///     Set to 0 to disable off-site crawling.
    /// </summary>
    public int OffSiteDepth { get; init; } = 1;

    /// <summary>
    ///     If true, the orchestrator clears existing chunks for
    ///     (LibraryId, Version) before starting and skips resume-mode
    ///     URL deduplication so every page is re-fetched. Used by
    ///     <c>scrape_docs force=true</c> to drive a true re-ingest after
    ///     the source docs change. Default false — most callers want
    ///     resume semantics.
    /// </summary>
    public bool ForceClean { get; init; }

    /// <summary>
    ///     If true, the orchestrator seeds the crawl queue from every
    ///     stored page URL for (LibraryId, Version) and the resume
    ///     skip-set is disabled so every seeded URL is re-fetched.
    ///     Used by <c>rescrape_library</c> to refresh an existing library
    ///     without needing the original RootUrl or crawl patterns — the
    ///     stored pages themselves drive the crawl, with link-following
    ///     picking up any newly added pages. Has no effect when
    ///     (LibraryId, Version) has no stored pages. Default false.
    /// </summary>
    public bool SeedFromStoredPages { get; init; }

    /// <summary>
    ///     Additional URLs to add to the crawl queue alongside
    ///     <see cref="RootUrl" />. Used for sites whose home page does
    ///     not link to all sections (e.g., DocFX-style sites where the
    ///     <c>/api/</c> tree is reachable only through namespace index
    ///     pages, not from the navigation bar). When non-null, the
    ///     orchestrator unions these URLs with the seeds derived from
    ///     <see cref="SeedFromStoredPages" /> and feeds the combined set
    ///     to <see cref="Crawling.IPageCrawler.CrawlAsync" />. The
    ///     <see cref="AllowedUrlPatterns" /> filter still applies, so
    ///     extra seeds outside the allowed scope are dropped at the
    ///     audit boundary.
    /// </summary>
    public IReadOnlyList<string>? SeedUrls { get; init; }

    /// <summary>
    ///     Additional HTTP status codes to treat as rate-limit signals, on top of
    ///     the built-in defaults (429, 503). Use for site-specific soft-limit
    ///     responses: 502 for Infragistics and similar CDNs, 520–522 for
    ///     Cloudflare rate walls.
    /// </summary>
    public IReadOnlyList<int>? AdditionalRateLimitStatusCodes { get; init; }

    /// <summary>
    ///     CSS selector that must resolve in the rendered DOM before content
    ///     extraction. When non-null, page 1 is still fetched with the SSR
    ///     navigator, escalation fires after that observation, and the URL
    ///     is requeued for re-fetch under the SPA navigator. Subsequent
    ///     fetches use the SPA navigator (NetworkIdle + 300 ms settle +
    ///     optional <see cref="SpaWaitMs" /> + selector wait) and the
    ///     selector is also tried first by the content extractor.
    ///     Use for known SPA documentation sites (e.g.
    ///     <c>.mud-main-content</c> for MudBlazor). Null means "auto-detect
    ///     via shell sniffing on the first 3 pages".
    /// </summary>
    public string? WaitForSelector { get; init; }

    /// <summary>
    ///     Additional milliseconds to wait after the SPA navigator's NetworkIdle
    ///     and built-in 300ms settle, before attempting content extraction.
    ///     Default 0 (no extra wait). Increase for heavy WASM sites or SPAs
    ///     that defer data fetches beyond Load. Only effective once the SPA
    ///     navigator is active (either explicitly via <see cref="WaitForSelector" />
    ///     or after auto-escalation).
    /// </summary>
    public int SpaWaitMs { get; init; }

    /// <summary>
    ///     Default per-page fetch delay. Zero means "no fixed delay" — pacing is
    ///     handled adaptively by <c>HostRateLimiter</c> based on per-host response
    ///     status (slows down on 429/503, speeds up on sustained success). A
    ///     non-zero value is still honored as an extra floor delay if a caller
    ///     wants to force pacing on top of the limiter.
    /// </summary>
    public const int DefaultFetchDelayMs = 0;
}
