// FakeScrapeAuditRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeScrapeAuditRepository : IScrapeAuditRepository
{
    private readonly Dictionary<string, AuditSummary> mSummaries = new();

    public void SetSummary(string jobId, AuditSummary summary)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(summary);
        mSummaries[jobId] = summary;
    }

    public Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default) =>
        mSummaries.TryGetValue(jobId, out var s)
            ? Task.FromResult(s)
            : throw new InvalidOperationException($"FakeScrapeAuditRepository: no summary for {jobId}");

    public Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeAuditRepository: InsertManyAsync not supported in this test");

    public Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId,
                                                               AuditStatus? status,
                                                               AuditSkipReason? skipReason,
                                                               string? host,
                                                               string? urlSubstring,
                                                               int limit,
                                                               CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeAuditRepository: QueryAsync not supported in this test");

    public Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeAuditRepository: GetByUrlAsync not supported in this test");

    public Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeAuditRepository: DeleteByJobIdAsync not supported in this test");

    public Task<long> DeleteByLibraryVersionAsync(string libraryId,
                                                  string version,
                                                  CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeAuditRepository: DeleteByLibraryVersionAsync not supported in this test");
}
