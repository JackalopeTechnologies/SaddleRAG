// CompactCollectionsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool that compacts MongoDB collections to reclaim disk space
///     freed by deletes. WiredTiger never returns deleted-document space
///     to the OS — it reuses it for future inserts within the same file.
///     For SaddleRAG's heavy create/delete-library churn, this means the
///     pages, chunks, scrape_audit_log, and bm25Shards files can stay
///     bloated long after their data has shrunk. Running compact reclaims
///     that space file-by-file. The operation blocks the target
///     collection (not the whole DB) while running; on a typical
///     SaddleRAG database it completes in seconds per collection.
/// </summary>
[McpServerToolType]
public static class CompactCollectionsTools
{
    private sealed record CollectionStats(
        string Collection,
        long Count,
        long Size,
        long StorageSize,
        long TotalIndexSize);

    private sealed record CompactResult(
        string Collection,
        bool Ok,
        long BytesFreed,
        long StorageBefore,
        long StorageAfter,
        long IndexBefore,
        long IndexAfter,
        long ElapsedMs,
        string? Error);

    [McpServerTool(Name = "compact_collections")]
    [Description("Compact MongoDB collections to reclaim disk space freed by deletes. " +
                 "WiredTiger reuses freed space for future inserts but never returns it " +
                 "to the OS without an explicit compact. Use after heavy churn (multiple " +
                 "delete_library or cleanup_orphans cycles) to shrink on-disk footprint. " +
                 "Defaults to the four hot collections (pages, chunks, scrape_audit_log, " +
                 "bm25Shards) — pass an explicit list to compact additional collections. " +
                 "compact blocks the target collection only (not the whole DB) while " +
                 "running; expect a few seconds per collection on a typical SaddleRAG " +
                 "database. Defaults to dryRun=true — preview current sizes before " +
                 "passing dryRun=false to apply."
                )]
    public static async Task<string> CompactCollections(RepositoryFactory repositoryFactory,
                                                        [Description("Collections to compact. Defaults to the four " +
                                                                     "hot collections (pages, chunks, " +
                                                                     "scrape_audit_log, bm25Shards) where SaddleRAG " +
                                                                     "churn produces meaningful reclaimable space."
                                                                    )]
                                                        string[]? collections = null,
                                                        [Description("If true (default), preview current storage " +
                                                                     "sizes without running compact."
                                                                    )]
                                                        bool dryRun = true,
                                                        [Description("Optional database profile name")]
                                                        string? profile = null,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var targetCollections = collections is { Length: > 0 } ? collections : smDefaultHotCollections;
        var database = repositoryFactory.GetDatabase(profile);

        string result;
        if (dryRun)
            result = await BuildDryRunResultAsync(database, targetCollections, ct);
        else
            result = await BuildCompactResultAsync(database, targetCollections, ct);
        return result;
    }

    private static async Task<string> BuildDryRunResultAsync(IMongoDatabase database,
                                                             IReadOnlyList<string> collections,
                                                             CancellationToken ct)
    {
        var stats = new List<CollectionStats>();
        foreach(var name in collections)
        {
            var entry = await GetCollectionStatsAsync(database, name, ct);
            stats.Add(entry);
        }

        long totalStorage = stats.Sum(s => s.StorageSize);
        long totalIndex = stats.Sum(s => s.TotalIndexSize);
        var preview = new
                          {
                              DryRun = true,
                              Collections = stats.Select(s => new
                                                                  {
                                                                      s.Collection,
                                                                      s.Count,
                                                                      Size_MB = ToMb(s.Size),
                                                                      StorageSize_MB = ToMb(s.StorageSize),
                                                                      TotalIndexSize_MB = ToMb(s.TotalIndexSize)
                                                                  }
                                                       ),
                              Total = new
                                          {
                                              Storage_MB = ToMb(totalStorage),
                                              Index_MB = ToMb(totalIndex)
                                          },
                              Note = DryRunNote
                          };
        string result = JsonSerializer.Serialize(preview, smJsonOptions);
        return result;
    }

    private static async Task<string> BuildCompactResultAsync(IMongoDatabase database,
                                                              IReadOnlyList<string> collections,
                                                              CancellationToken ct)
    {
        var results = new List<CompactResult>();
        foreach(var name in collections)
        {
            var entry = await CompactCollectionAsync(database, name, ct);
            results.Add(entry);
        }

        long totalBytesFreed = results.Sum(r => r.BytesFreed);
        long totalStorageBefore = results.Sum(r => r.StorageBefore);
        long totalStorageAfter = results.Sum(r => r.StorageAfter);
        long totalIndexBefore = results.Sum(r => r.IndexBefore);
        long totalIndexAfter = results.Sum(r => r.IndexAfter);
        var response = new
                           {
                               DryRun = false,
                               Collections = results.Select(r => new
                                                                     {
                                                                         r.Collection,
                                                                         Ok = r.Ok,
                                                                         BytesFreed_MB = ToMb(r.BytesFreed),
                                                                         StorageBefore_MB = ToMb(r.StorageBefore),
                                                                         StorageAfter_MB = ToMb(r.StorageAfter),
                                                                         IndexBefore_MB = ToMb(r.IndexBefore),
                                                                         IndexAfter_MB = ToMb(r.IndexAfter),
                                                                         ElapsedMs = r.ElapsedMs,
                                                                         Error = r.Error
                                                                     }
                                                          ),
                               Total = new
                                           {
                                               BytesFreed_MB = ToMb(totalBytesFreed),
                                               StorageBefore_MB = ToMb(totalStorageBefore),
                                               StorageAfter_MB = ToMb(totalStorageAfter),
                                               IndexBefore_MB = ToMb(totalIndexBefore),
                                               IndexAfter_MB = ToMb(totalIndexAfter)
                                           }
                           };
        string result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    private static async Task<CompactResult> CompactCollectionAsync(IMongoDatabase database,
                                                                    string name,
                                                                    CancellationToken ct)
    {
        var before = await GetCollectionStatsAsync(database, name, ct);
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
                                  { FieldCompact, name },
                                  { FieldForce, true }
                              };
            var response = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct);
            ok = Math.Abs(response.GetValue(FieldOk, 0).ToDouble() - 1.0) < OkTolerance;
            if (response.Contains(FieldBytesFreed))
                bytesFreed = response[FieldBytesFreed].ToInt64();
        }
        catch(MongoException ex)
        {
            error = ex.Message;
        }
        sw.Stop();
        var after = await GetCollectionStatsAsync(database, name, ct);
        var result = new CompactResult(name,
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

    private static async Task<CollectionStats> GetCollectionStatsAsync(IMongoDatabase database,
                                                                       string name,
                                                                       CancellationToken ct)
    {
        var command = new BsonDocument { { FieldCollStats, name } };
        BsonDocument? response = null;
        try
        {
            response = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: ct);
        }
        catch(MongoException)
        {
            // Collection doesn't exist or otherwise unreadable; the null
            // response below is treated as zero-size, so a typo in
            // collections=[...] degrades gracefully instead of failing
            // the whole call.
        }
        CollectionStats stats;
        if (response == null)
            stats = new CollectionStats(name, 0, 0, 0, 0);
        else
        {
            long count = response.GetValue(FieldCount, 0).ToInt64();
            long size = response.GetValue(FieldSize, 0).ToInt64();
            long storageSize = response.GetValue(FieldStorageSize, 0).ToInt64();
            long indexSize = response.GetValue(FieldTotalIndexSize, 0).ToInt64();
            stats = new CollectionStats(name, count, size, storageSize, indexSize);
        }
        return stats;
    }

    private static string ToMb(long bytes) => ((double) bytes / BytesPerMb).ToString(MegabyteFormat);

    private const int BytesPerKb = 1024;
    private const int BytesPerMb = BytesPerKb * BytesPerKb;
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

    private const string MegabyteFormat = "F2";

    private const string CollectionPages = "pages";
    private const string CollectionChunks = "chunks";
    private const string CollectionScrapeAuditLog = "scrape_audit_log";
    private const string CollectionBm25Shards = "bm25Shards";

    private const string DryRunNote = "Run with dryRun=false to compact. Compact blocks each collection briefly while running. Bytes freed will be reported per collection in the result.";

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

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
