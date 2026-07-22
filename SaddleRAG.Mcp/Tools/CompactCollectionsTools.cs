// CompactCollectionsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Packaging;

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
    public static Task<string> CompactCollectionsFromMcp(RepositoryFactory repositoryFactory,
                                                         ICollectionCompactor compactor,
                                                         [Description("Collections to compact. Defaults to the four " +
                                                                      "hot collections (pages, chunks, " +
                                                                      "scrape_audit_log, bm25Shards) where SaddleRAG " +
                                                                      "churn produces meaningful reclaimable space."
                                                                     )]
                                                         JsonElement? collections = null,
                                                         [Description("If true (default), preview current storage " +
                                                                      "sizes without running compact."
                                                                     )]
                                                         bool dryRun = true,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(compactor);

        string[]? parsedCollections = McpStringArrayArgumentParser.Parse(collections, nameof(collections));
        Task<string> result = CompactCollections(repositoryFactory, compactor, parsedCollections, dryRun, profile, ct);
        return result;
    }

    public static async Task<string> CompactCollections(RepositoryFactory repositoryFactory,
                                                        ICollectionCompactor compactor,
                                                        string[]? collections = null,
                                                        bool dryRun = true,
                                                        string? profile = null,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(compactor);

        var targetCollections = collections is { Length: > 0 } ? collections : compactor.DefaultHotCollections;
        var database = repositoryFactory.GetDatabase(profile);

        string result;
        if (dryRun)
            result = await BuildDryRunResultAsync(compactor, database, targetCollections, ct);
        else
            result = await BuildCompactResultAsync(compactor, database, targetCollections, ct);
        return result;
    }

    private static async Task<string> BuildDryRunResultAsync(ICollectionCompactor compactor,
                                                              MongoDB.Driver.IMongoDatabase database,
                                                              IReadOnlyList<string> collections,
                                                              CancellationToken ct)
    {
        var stats = new List<CollectionStats>();
        foreach (var name in collections)
        {
            var entry = await compactor.GetStatsAsync(database, name, ct);
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

    private static async Task<string> BuildCompactResultAsync(ICollectionCompactor compactor,
                                                               MongoDB.Driver.IMongoDatabase database,
                                                               IReadOnlyList<string> collections,
                                                               CancellationToken ct)
    {
        var results = new List<CompactResult>();
        foreach (var name in collections)
        {
            var entry = await compactor.CompactAsync(database, name, ct);
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

    private static string ToMb(long bytes) => ((double) bytes / BytesPerMb).ToString(MegabyteFormat);

    private const int BytesPerKb = 1024;
    private const int BytesPerMb = BytesPerKb * BytesPerKb;

    private const string MegabyteFormat = "F2";

    private const string DryRunNote = "Run with dryRun=false to compact. Compact blocks each collection briefly while running. Bytes freed will be reported per collection in the result.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
