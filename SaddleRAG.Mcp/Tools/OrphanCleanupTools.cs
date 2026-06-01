// OrphanCleanupTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool that detects and removes orphaned (LibraryId, Version) rows
///     in child collections (pages, chunks, libraryProfiles, libraryIndexes,
///     bm25Shards, library_excluded_symbols, scrape_audit_log) whose parent
///     <see cref="LibraryRecord" /> no longer exists. Common cause: a scrape
///     that was cancelled mid-flight after data landed but before the
///     library row committed, leaving chunks/pages indexed but unreachable
///     through list_libraries / search_docs / get_library_overview.
/// </summary>
[McpServerToolType]
public static class OrphanCleanupTools
{
    private sealed record OrphanReport(
        IReadOnlyList<LibraryVersionKey> Pages,
        IReadOnlyList<LibraryVersionKey> Chunks,
        IReadOnlyList<LibraryVersionKey> Profiles,
        IReadOnlyList<LibraryVersionKey> Indexes,
        IReadOnlyList<LibraryVersionKey> Bm25Shards,
        IReadOnlyList<LibraryVersionKey> ExcludedSymbols,
        IReadOnlyList<LibraryVersionKey> AuditLog);

    private sealed record DeletionTotals(
        long Pages,
        long Chunks,
        long Profiles,
        long Indexes,
        long Bm25Shards,
        long ExcludedSymbols,
        long AuditEntries);

    [McpServerTool(Name = "cleanup_orphans")]
    [Description("Detect and clean up (LibraryId, Version) rows in child collections " +
                 "whose parent libraries row is missing. Scans pages, chunks, libraryProfiles, " +
                 "libraryIndexes, bm25Shards, library_excluded_symbols, and scrape_audit_log. " +
                 "Use when a cancelled scrape (e.g. pipeline counters show data ingested but " +
                 "list_pages reports 'Library not found') leaves stranded data behind. " +
                 "Optional library/version filters narrow the scan to a specific stranded pair. " +
                 "Defaults to dryRun=true — preview the per-collection orphan counts before " +
                 "passing dryRun=false to apply. dryRun=false returns { JobId, Status: 'Queued' } " +
                 "immediately; poll get_job_status for the outcome."
                )]
    public static async Task<string> CleanupOrphans(RepositoryFactory repositoryFactory,
                                                    [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                    IBackgroundJobRunner runner,
                                                    [Description("Optional library identifier to scope the scan.")]
                                                    string? library = null,
                                                    [Description("Optional library version to scope the scan. " +
                                                                 "When set without 'library', filters to that " +
                                                                 "version across every library."
                                                                )]
                                                    string? version = null,
                                                    [Description("If true (default), preview without writing.")]
                                                    bool dryRun = true,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);

        string result;
        if (dryRun)
            result = await BuildDryRunResultAsync(repositoryFactory, profile, library, version, ct);
        else
        {
            result = await QueueApplyJobAsync(repositoryFactory,
                                              runner,
                                              profile,
                                              library,
                                              version,
                                              ct
                                             );
        }

        return result;
    }

    private static async Task<string> BuildDryRunResultAsync(RepositoryFactory factory,
                                                             string? profile,
                                                             string? library,
                                                             string? version,
                                                             CancellationToken ct)
    {
        var parents = await GetValidParentsAsync(factory, profile, ct);
        var orphans = await CollectOrphansAsync(factory,
                                                profile,
                                                parents,
                                                library,
                                                version,
                                                ct
                                               );
        var preview = new
                          {
                              DryRun = true,
                              Filter = new
                                           {
                                               Library = library,
                                               Version = version
                                           },
                              WouldDelete = SummarizeOrphans(orphans)
                          };
        var result = JsonSerializer.Serialize(preview, smJsonOptions);
        return result;
    }

    private static async Task<string> QueueApplyJobAsync(RepositoryFactory factory,
                                                         IBackgroundJobRunner runner,
                                                         string? profile,
                                                         string? library,
                                                         string? version,
                                                         CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { library, version, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.CleanupOrphans,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, _, jobCt) =>
                                            {
                                                var parents =
                                                    await GetValidParentsAsync(factory, profile, jobCt);
                                                var orphans = await CollectOrphansAsync(factory,
                                                                       profile,
                                                                       parents,
                                                                       library,
                                                                       version,
                                                                       jobCt
                                                                  );
                                                var deleted =
                                                    await DeleteOrphansAsync(factory, profile, orphans, jobCt);
                                                record.ResultJson = JsonSerializer.Serialize(new
                                                             {
                                                                 DryRun = false,
                                                                 Filter = new
                                                                              {
                                                                                  Library = library,
                                                                                  Version = version
                                                                              },
                                                                 Deleted = deleted
                                                             },
                                                         smJsonOptions
                                                    );
                                            },
                                            ct
                                           );

        var response = JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) },
                                                smJsonOptions
                                               );
        return response;
    }

    private static async Task<HashSet<LibraryVersionKey>> GetValidParentsAsync(RepositoryFactory factory,
                                                                               string? profile,
                                                                               CancellationToken ct)
    {
        var libraryRepo = factory.GetLibraryRepository(profile);
        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        var parents = libraries.SelectMany(lib => lib.AllVersions.Select(v => new LibraryVersionKey(lib.Id, v)))
                               .ToHashSet();
        return parents;
    }

    private static async Task<OrphanReport> CollectOrphansAsync(RepositoryFactory factory,
                                                                string? profile,
                                                                HashSet<LibraryVersionKey> parents,
                                                                string? library,
                                                                string? version,
                                                                CancellationToken ct)
    {
        var pageRepo = factory.GetPageRepository(profile);
        var chunkRepo = factory.GetChunkRepository(profile);
        var profileRepo = factory.GetLibraryProfileRepository(profile);
        var indexRepo = factory.GetLibraryIndexRepository(profile);
        var bm25Repo = factory.GetBm25ShardRepository(profile);
        var excludedRepo = factory.GetExcludedSymbolsRepository(profile);
        var auditRepo = factory.GetScrapeAuditRepository(profile);

        var pagePairs = await pageRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var chunkPairs = await chunkRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var profilePairs = await profileRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var indexPairs = await indexRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var shardPairs = await bm25Repo.GetDistinctLibraryVersionPairsAsync(ct);
        var excludedPairs = await excludedRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var auditPairs = await auditRepo.GetDistinctLibraryVersionPairsAsync(ct);

        var report = new OrphanReport(FilterOrphans(pagePairs, parents, library, version),
                                      FilterOrphans(chunkPairs, parents, library, version),
                                      FilterOrphans(profilePairs, parents, library, version),
                                      FilterOrphans(indexPairs, parents, library, version),
                                      FilterOrphans(shardPairs, parents, library, version),
                                      FilterOrphans(excludedPairs, parents, library, version),
                                      FilterOrphans(auditPairs, parents, library, version)
                                     );
        return report;
    }

    private static IReadOnlyList<LibraryVersionKey> FilterOrphans(IReadOnlyList<LibraryVersionKey> pairs,
                                                                  HashSet<LibraryVersionKey> parents,
                                                                  string? library,
                                                                  string? version)
    {
        var libraryFiltered = string.IsNullOrEmpty(library)
                                  ? pairs
                                  : pairs.Where(p => p.LibraryId == library);
        var versionFiltered = string.IsNullOrEmpty(version)
                                  ? libraryFiltered
                                  : libraryFiltered.Where(p => p.Version == version);
        var orphans = versionFiltered.Where(p => !parents.Contains(p))
                                     .OrderBy(p => p.LibraryId, StringComparer.Ordinal)
                                     .ThenBy(p => p.Version, StringComparer.Ordinal)
                                     .ToList();
        return orphans;
    }

    private static async Task<DeletionTotals> DeleteOrphansAsync(RepositoryFactory factory,
                                                                 string? profile,
                                                                 OrphanReport orphans,
                                                                 CancellationToken ct)
    {
        var pageRepo = factory.GetPageRepository(profile);
        var chunkRepo = factory.GetChunkRepository(profile);
        var profileRepo = factory.GetLibraryProfileRepository(profile);
        var indexRepo = factory.GetLibraryIndexRepository(profile);
        var bm25Repo = factory.GetBm25ShardRepository(profile);
        var excludedRepo = factory.GetExcludedSymbolsRepository(profile);
        var auditRepo = factory.GetScrapeAuditRepository(profile);

        long pages = await DeletePerKeyAsync(orphans.Pages,
                                             (k, jobCt) => pageRepo.DeleteAsync(k.LibraryId, k.Version, jobCt),
                                             ct
                                            );
        long chunks = await DeletePerKeyAsync(orphans.Chunks,
                                              (k, jobCt) =>
                                                  chunkRepo.DeleteChunksAsync(k.LibraryId, k.Version, jobCt),
                                              ct
                                             );
        long profiles = await DeletePerKeyAsync(orphans.Profiles,
                                                (k, jobCt) =>
                                                    profileRepo.DeleteAsync(k.LibraryId, k.Version, jobCt),
                                                ct
                                               );
        long indexes = await DeletePerKeyAsync(orphans.Indexes,
                                               (k, jobCt) =>
                                                   indexRepo.DeleteAsync(k.LibraryId, k.Version, jobCt),
                                               ct
                                              );
        long shards = await DeletePerKeyAsync(orphans.Bm25Shards,
                                              (k, jobCt) =>
                                                  bm25Repo.DeleteAsync(k.LibraryId, k.Version, jobCt),
                                              ct
                                             );
        long excluded = await DeletePerKeyAsync(orphans.ExcludedSymbols,
                                                (k, jobCt) =>
                                                    excludedRepo.DeleteAsync(k.LibraryId, k.Version, jobCt),
                                                ct
                                               );
        long audit = await DeletePerKeyAsync(orphans.AuditLog,
                                             (k, jobCt) =>
                                                 auditRepo.DeleteByLibraryVersionAsync(k.LibraryId,
                                                          k.Version,
                                                          jobCt
                                                     ),
                                             ct
                                            );

        var totals = new DeletionTotals(pages,
                                        chunks,
                                        profiles,
                                        indexes,
                                        shards,
                                        excluded,
                                        audit
                                       );
        return totals;
    }

    private static async Task<long> DeletePerKeyAsync(IReadOnlyList<LibraryVersionKey> keys,
                                                      Func<LibraryVersionKey, CancellationToken, Task<long>> deleter,
                                                      CancellationToken ct)
    {
        long total = 0;
        foreach(var key in keys)
            total += await deleter(key, ct);
        return total;
    }

    private static object SummarizeOrphans(OrphanReport orphans)
    {
        var allKeys = orphans.Pages
                             .Concat(orphans.Chunks)
                             .Concat(orphans.Profiles)
                             .Concat(orphans.Indexes)
                             .Concat(orphans.Bm25Shards)
                             .Concat(orphans.ExcludedSymbols)
                             .Concat(orphans.AuditLog)
                             .Distinct()
                             .OrderBy(k => k.LibraryId, StringComparer.Ordinal)
                             .ThenBy(k => k.Version, StringComparer.Ordinal)
                             .ToList();

        var summary = new
                          {
                              OrphanedPairs = allKeys.Count,
                              ByCollection = new
                                                 {
                                                     Pages = orphans.Pages.Count,
                                                     Chunks = orphans.Chunks.Count,
                                                     Profiles = orphans.Profiles.Count,
                                                     Indexes = orphans.Indexes.Count,
                                                     Bm25Shards = orphans.Bm25Shards.Count,
                                                     ExcludedSymbols = orphans.ExcludedSymbols.Count,
                                                     AuditEntries = orphans.AuditLog.Count
                                                 },
                              Pairs = allKeys.Select(k => new
                                                              {
                                                                  k.LibraryId,
                                                                  k.Version,
                                                                  HasPages = orphans.Pages.Contains(k),
                                                                  HasChunks = orphans.Chunks.Contains(k),
                                                                  HasProfile = orphans.Profiles.Contains(k),
                                                                  HasIndex = orphans.Indexes.Contains(k),
                                                                  HasBm25Shards = orphans.Bm25Shards.Contains(k),
                                                                  HasExcludedSymbols =
                                                                      orphans.ExcludedSymbols.Contains(k),
                                                                  HasAuditEntries = orphans.AuditLog.Contains(k)
                                                              }
                                                    )
                                             .ToList()
                          };
        return summary;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
