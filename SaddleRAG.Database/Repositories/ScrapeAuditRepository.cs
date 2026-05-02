// ScrapeAuditRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of <see cref="IScrapeAuditRepository"/>.
/// </summary>
public sealed class ScrapeAuditRepository : IScrapeAuditRepository
{
    public ScrapeAuditRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task InsertManyAsync(IEnumerable<ScrapeAuditLogEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var insertTask = list.Count > 0
            ? mContext.ScrapeAuditLog.InsertManyAsync(list, new InsertManyOptions { IsOrdered = false }, ct)
            : Task.CompletedTask;
        await insertTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeAuditLogEntry>> QueryAsync(string jobId,
                                                                     AuditStatus? status,
                                                                     AuditSkipReason? skipReason,
                                                                     string? host,
                                                                     string? urlSubstring,
                                                                     int limit,
                                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var filter = Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.JobId, jobId);
        if (status.HasValue)
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Status, status.Value);
        if (skipReason.HasValue)
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.SkipReason, skipReason.Value);
        if (!string.IsNullOrEmpty(host))
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Host, host);
        if (!string.IsNullOrEmpty(urlSubstring))
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(urlSubstring);
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Regex(a => a.Url,
                                                                  new BsonRegularExpression(escaped));
        }
        return await mContext.ScrapeAuditLog.Find(filter)
                                            .Limit(limit > 0 ? limit : 50)
                                            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ScrapeAuditLogEntry?> GetByUrlAsync(string jobId, string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(url);
        var filter = Builders<ScrapeAuditLogEntry>.Filter.And(
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.JobId, jobId),
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Url, url));
        return await mContext.ScrapeAuditLog.Find(filter).FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var entries = await QueryAsync(jobId, null, null, null, null, int.MaxValue, ct);

        var skipReasonCounts = entries
            .Select(e => e.SkipReason)
            .OfType<AuditSkipReason>()
            .GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count());

        var hostCounts = entries.GroupBy(e => e.Host)
                                .ToDictionary(g => g.Key, g => g.Count());

        return new AuditSummary
        {
            JobId            = jobId,
            TotalConsidered  = entries.Count,
            IndexedCount     = entries.Count(e => e.Status == AuditStatus.Indexed),
            FetchedCount     = entries.Count(e => e.Status == AuditStatus.Fetched),
            FailedCount      = entries.Count(e => e.Status == AuditStatus.Failed),
            SkippedCount     = entries.Count(e => e.Status == AuditStatus.Skipped),
            SkipReasonCounts = skipReasonCounts,
            HostCounts       = hostCounts
        };
    }

    /// <inheritdoc />
    public async Task<long> DeleteByJobIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var result = await mContext.ScrapeAuditLog.DeleteManyAsync(a => a.JobId == jobId, ct);
        return result.DeletedCount;
    }

    /// <inheritdoc />
    public async Task<long> DeleteByLibraryVersionAsync(string libraryId,
                                                        string version,
                                                        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var filter = Builders<ScrapeAuditLogEntry>.Filter.And(
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.LibraryId, libraryId),
            Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Version, version));
        var result = await mContext.ScrapeAuditLog.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }
}
