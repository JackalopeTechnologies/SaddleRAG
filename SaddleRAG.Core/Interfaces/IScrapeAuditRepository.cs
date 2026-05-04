// IScrapeAuditRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Audit;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Data access for per-URL scrape audit log entries.
/// </summary>
public interface IScrapeAuditRepository
{
    /// <summary>
    ///     Insert a batch of audit entries.
    /// </summary>
    Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default);

    /// <summary>
    ///     Query entries for a job with optional filters.
    /// </summary>
    /// <param name="jobId">The scrape job identifier.</param>
    /// <param name="status">Optional filter by audit status.</param>
    /// <param name="skipReason">Optional filter by skip reason.</param>
    /// <param name="host">Optional filter by hostname.</param>
    /// <param name="urlSubstring">Optional filter by URL substring.</param>
    /// <param name="limit">
    ///     Maximum entries to return. Pass <see cref="int.MaxValue" /> for unlimited.
    ///     Values ≤ 0 fall back to a default cap of 50.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId,
                                                        AuditStatus? status,
                                                        AuditSkipReason? skipReason,
                                                        string? host,
                                                        string? urlSubstring,
                                                        int limit,
                                                        CancellationToken ct = default);

    /// <summary>
    ///     Return the single entry for a (jobId, url) pair, or null.
    /// </summary>
    Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default);

    /// <summary>
    ///     Return bucketed counts for all entries in the job.
    /// </summary>
    Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    ///     Delete all entries for a job. Returns the number of documents removed.
    /// </summary>
    Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    ///     Delete all entries for a (libraryId, version) pair across any job.
    ///     Used when a rescrape replaces all prior audit data.
    ///     Returns the number of documents removed.
    /// </summary>
    Task<long> DeleteByLibraryVersionAsync(string libraryId, string version, CancellationToken ct = default);
}
