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
