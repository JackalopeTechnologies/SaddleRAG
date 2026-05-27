// CollectionCompactor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     MongoDB per-collection compaction and stats service.
///     Shared between the <c>compact_collections</c> MCP tool and the
///     <c>import_library</c> compact opt-in so both surfaces exercise
///     exactly the same code path.
/// </summary>
public sealed class CollectionCompactor : ICollectionCompactor
{
    #region ICollectionCompactor

    /// <inheritdoc />
    public IReadOnlyList<string> DefaultHotCollections => smDefaultHotCollections;

    /// <inheritdoc />
    public async Task<CollectionStats> GetStatsAsync(IMongoDatabase database,
                                                      string collectionName,
                                                      CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrEmpty(collectionName);

        var command = new BsonDocument { { FieldCollStats, collectionName } };
        BsonDocument? response = null;
        try
        {
            response = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct);
        }
        catch (MongoException)
        {
            // Collection doesn't exist or otherwise unreadable. A null response
            // below is treated as zero-size so a typo in the caller's collection
            // list degrades gracefully instead of failing the whole call.
        }

        CollectionStats stats;
        if (response == null)
            stats = new CollectionStats(collectionName, 0, 0, 0, 0);
        else
        {
            long count = response.GetValue(FieldCount, 0).ToInt64();
            long size = response.GetValue(FieldSize, 0).ToInt64();
            long storageSize = response.GetValue(FieldStorageSize, 0).ToInt64();
            long indexSize = response.GetValue(FieldTotalIndexSize, 0).ToInt64();
            stats = new CollectionStats(collectionName, count, size, storageSize, indexSize);
        }
        return stats;
    }

    /// <inheritdoc />
    public async Task<CompactResult> CompactAsync(IMongoDatabase database,
                                                   string collectionName,
                                                   CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrEmpty(collectionName);

        var before = await GetStatsAsync(database, collectionName, ct);
        var sw = Stopwatch.StartNew();
        bool ok = false;
        string? error = null;
        long bytesFreed = 0;
        try
        {
            // force=true allows compact on a primary in a replica set; harmless
            // on a standalone deployment. Without it, mongod refuses to compact
            // a primary by default — which would be confusing the first time a
            // user wires SaddleRAG up to a real cluster.
            var command = new BsonDocument
                              {
                                  { FieldCompact, collectionName },
                                  { FieldForce, true }
                              };
            var response = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct);
            ok = Math.Abs(response.GetValue(FieldOk, 0).ToDouble() - 1.0) < OkTolerance;
            if (response.Contains(FieldBytesFreed))
                bytesFreed = response[FieldBytesFreed].ToInt64();
        }
        catch (MongoException ex)
        {
            error = ex.Message;
        }
        sw.Stop();
        var after = await GetStatsAsync(database, collectionName, ct);
        var result = new CompactResult(collectionName,
                                       ok,
                                       bytesFreed,
                                       before.StorageSize,
                                       after.StorageSize,
                                       before.TotalIndexSize,
                                       after.TotalIndexSize,
                                       sw.ElapsedMilliseconds,
                                       error
                                      );
        return result;
    }

    #endregion

    #region Constants

    private const double OkTolerance = 1e-9;

    private const string FieldCompact = "compact";
    private const string FieldForce = "force";
    private const string FieldCollStats = "collStats";
    private const string FieldOk = "ok";
    private const string FieldBytesFreed = "bytesFreed";
    private const string FieldCount = "count";
    private const string FieldSize = "size";
    private const string FieldStorageSize = "storageSize";
    private const string FieldTotalIndexSize = "totalIndexSize";

    private const string CollectionPages = "pages";
    private const string CollectionChunks = "chunks";
    private const string CollectionScrapeAuditLog = "scrape_audit_log";
    private const string CollectionBm25Shards = "bm25Shards";

    #endregion

    /// <summary>
    ///     Default set of collections that benefit from compact. Determined
    ///     empirically from SaddleRAG churn patterns: these four collections
    ///     accumulate the bulk of file-level free space across
    ///     create/delete-library cycles. The smaller collections (libraries,
    ///     libraryVersions, jobs, libraryIndexes, libraryProfiles, etc.)
    ///     compact to zero bytes freed in practice, so they're left out of
    ///     the default set to keep the operation fast.
    /// </summary>
    private static readonly string[] smDefaultHotCollections =
        [CollectionPages, CollectionChunks, CollectionScrapeAuditLog, CollectionBm25Shards];
}
