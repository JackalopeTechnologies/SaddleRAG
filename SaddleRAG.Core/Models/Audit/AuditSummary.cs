// AuditSummary.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Aggregated counts for all audit log entries belonging to a single scrape job.
/// </summary>
public sealed record AuditSummary
{
    /// <summary>
    ///     The scrape job this summary covers.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    ///     Total number of URLs considered during the job.
    /// </summary>
    public required int TotalConsidered { get; init; }

    /// <summary>
    ///     Number of URLs that reached <see cref="AuditStatus.Indexed"/>.
    /// </summary>
    public required int IndexedCount { get; init; }

    /// <summary>
    ///     Number of URLs that reached <see cref="AuditStatus.Fetched"/> but were not indexed.
    /// </summary>
    public required int FetchedCount { get; init; }

    /// <summary>
    ///     Number of URLs that reached <see cref="AuditStatus.Failed"/>.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>
    ///     Number of URLs that reached <see cref="AuditStatus.Skipped"/>.
    /// </summary>
    public required int SkippedCount { get; init; }

    /// <summary>
    ///     Count of skipped URLs broken down by <see cref="AuditSkipReason"/>.
    /// </summary>
    public required IReadOnlyDictionary<AuditSkipReason, int> SkipReasonCounts { get; init; }

    /// <summary>
    ///     Count of URLs considered per hostname.
    /// </summary>
    public required IReadOnlyDictionary<string, int> HostCounts { get; init; }
}
