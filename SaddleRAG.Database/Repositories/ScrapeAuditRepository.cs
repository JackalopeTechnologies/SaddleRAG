// ScrapeAuditRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of <see cref="IScrapeAuditRepository" />.
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
                             ? mContext.ScrapeAuditLog.InsertManyAsync(list,
                                                                       new InsertManyOptions { IsOrdered = false },
                                                                       ct
                                                                      )
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
            var escaped = Regex.Escape(urlSubstring);
            filter &= Builders<ScrapeAuditLogEntry>.Filter.Regex(a => a.Url,
                                                                 new BsonRegularExpression(escaped)
                                                                );
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
        var filter =
            Builders<ScrapeAuditLogEntry>.Filter.And(Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.JobId, jobId),
                                                     Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Url, url)
                                                    );
        return await mContext.ScrapeAuditLog.Find(filter).FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<AuditSummary> SummarizeAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        // ── status bucket pipeline ───────────────────────────────────────────────
        var statusPipeline = new[]
                                 {
                                     new BsonDocument("$match", new BsonDocument("JobId", jobId)),
                                     new BsonDocument("$group",
                                                      new BsonDocument
                                                          {
                                                              {
                                                                  AggregateIdField,
                                                                  new BsonDocument(MongoStatusField, "$Status")
                                                              },
                                                              {
                                                                  AggregateCountField,
                                                                  new BsonDocument("$sum", value: 1)
                                                              }
                                                          }
                                                     )
                                 };

        // ── host bucket pipeline ─────────────────────────────────────────────────
        var hostPipeline = new[]
                               {
                                   new BsonDocument("$match", new BsonDocument("JobId", jobId)),
                                   new BsonDocument("$group",
                                                    new BsonDocument
                                                        {
                                                            { AggregateIdField, "$Host" },
                                                            { AggregateCountField, new BsonDocument("$sum", value: 1) }
                                                        }
                                                   )
                               };

        // ── skip-reason bucket pipeline ──────────────────────────────────────────
        var reasonPipeline = new[]
                                 {
                                     new BsonDocument("$match",
                                                      new BsonDocument
                                                          {
                                                              { "JobId", jobId },
                                                              { "SkipReason", new BsonDocument("$exists", value: true) }
                                                          }
                                                     ),
                                     new BsonDocument("$group",
                                                      new BsonDocument
                                                          {
                                                              { AggregateIdField, "$SkipReason" },
                                                              {
                                                                  AggregateCountField,
                                                                  new BsonDocument("$sum", value: 1)
                                                              }
                                                          }
                                                     )
                                 };

        var col = mContext.ScrapeAuditLog;
        var statusTask = col.Aggregate<BsonDocument>(statusPipeline, cancellationToken: ct).ToListAsync(ct);
        var hostTask = col.Aggregate<BsonDocument>(hostPipeline, cancellationToken: ct).ToListAsync(ct);
        var reasonTask = col.Aggregate<BsonDocument>(reasonPipeline, cancellationToken: ct).ToListAsync(ct);

        await Task.WhenAll(statusTask, hostTask, reasonTask);

        var statusBuckets = statusTask.Result;
        var hostBuckets = hostTask.Result;
        var reasonBuckets = reasonTask.Result;

        var total = 0;
        var indexed = 0;
        var fetched = 0;
        var failed = 0;
        var skipped = 0;

        foreach(var doc in statusBuckets)
        {
            int cnt = doc[AggregateCountField].AsInt32;
            total += cnt;
            var status = (AuditStatus) doc[AggregateIdField][MongoStatusField].AsInt32;
            switch(status)
            {
                case AuditStatus.Indexed:
                    indexed = cnt;
                    break;
                case AuditStatus.Fetched:
                    fetched = cnt;
                    break;
                case AuditStatus.Failed:
                    failed = cnt;
                    break;
                case AuditStatus.Skipped:
                    skipped = cnt;
                    break;
            }
        }

        var skipReasonCounts = reasonBuckets
                               .Where(d => !d[AggregateIdField].IsBsonNull)
                               .ToDictionary(d => (AuditSkipReason) d[AggregateIdField].AsInt32,
                                             d => d[AggregateCountField].AsInt32
                                            );

        var hostCounts =
            hostBuckets.ToDictionary(d => d[AggregateIdField].IsBsonNull ? string.Empty : d[AggregateIdField].AsString,
                                     d => d[AggregateCountField].AsInt32
                                    );

        return new AuditSummary
                   {
                       JobId = jobId,
                       TotalConsidered = total,
                       IndexedCount = indexed,
                       FetchedCount = fetched,
                       FailedCount = failed,
                       SkippedCount = skipped,
                       SkipReasonCounts = skipReasonCounts,
                       HostCounts = hostCounts
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
        var filter =
            Builders<ScrapeAuditLogEntry>.Filter.And(Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.LibraryId,
                                                              libraryId
                                                         ),
                                                     Builders<ScrapeAuditLogEntry>.Filter.Eq(a => a.Version, version)
                                                    );
        var result = await mContext.ScrapeAuditLog.DeleteManyAsync(filter, ct);
        return result.DeletedCount;
    }

    private const string AggregateIdField = "_id";
    private const string AggregateCountField = "count";
    private const string MongoStatusField = "Status";
}
