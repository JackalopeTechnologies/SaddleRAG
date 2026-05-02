// ScrapeAuditLogEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Per-URL audit record written by the scrape pipeline so the
///     inspect_scrape MCP tool can explain why any given URL was
///     skipped, failed, or indexed.
/// </summary>
public sealed class ScrapeAuditLogEntry
{
    /// <summary>
    ///     Unique identifier for this audit entry.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     The scrape job that produced this entry.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    ///     Library being scraped.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version being scraped.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     The URL that was considered.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     The URL that linked to this URL, if known.
    /// </summary>
    public string? ParentUrl { get; init; }

    /// <summary>
    ///     Hostname of the URL.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    ///     Crawl depth at which this URL was discovered.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    ///     When the URL was first discovered by the crawler.
    /// </summary>
    public required DateTime DiscoveredAt { get; init; }

    /// <summary>
    ///     Final disposition of this URL.
    /// </summary>
    public required AuditStatus Status { get; init; }

    /// <summary>
    ///     Why the URL was skipped. Null when Status is not Skipped.
    /// </summary>
    public AuditSkipReason? SkipReason { get; init; }

    /// <summary>
    ///     Human-readable detail about the skip decision (e.g. "depth=2 limit=1").
    /// </summary>
    public string? SkipDetail { get; init; }

    /// <summary>
    ///     Fetch and indexing outcome. Populated when Status is Fetched, Failed, or Indexed.
    /// </summary>
    public AuditPageOutcome? PageOutcome { get; init; }
}
