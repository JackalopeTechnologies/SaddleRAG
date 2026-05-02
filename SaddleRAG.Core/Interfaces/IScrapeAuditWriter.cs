// IScrapeAuditWriter.cs
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
///     Fire-and-forget writer that buffers scrape audit events and flushes
///     them to <see cref="IScrapeAuditRepository"/> in batches.
/// </summary>
public interface IScrapeAuditWriter : IAsyncDisposable
{
    /// <summary>
    ///     Record a URL that was skipped before fetching.
    /// </summary>
    void RecordSkipped(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                       AuditSkipReason reason, string? detail);

    /// <summary>
    ///     Record a URL that was successfully fetched but not yet indexed.
    /// </summary>
    void RecordFetched(AuditContext ctx, string url, string? parentUrl, string host, int depth);

    /// <summary>
    ///     Record a URL whose fetch or processing failed.
    /// </summary>
    void RecordFailed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                      string error);

    /// <summary>
    ///     Record a URL that was fetched and fully indexed.
    /// </summary>
    void RecordIndexed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                       AuditPageOutcome outcome);

    /// <summary>
    ///     Drain all buffered entries to the repository immediately.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);
}
