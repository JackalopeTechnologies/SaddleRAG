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
using SaddleRAG.Core.Models.Monitor;
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
        IReadOnlyList<LibraryVersionKey> AuditLog,
        DirectoryOrphanReport Documents);

    private sealed record DirectoryOrphanReport(
        IReadOnlyList<DirectoryLibraryDefinition> DirectoryLibraries,
        IReadOnlyList<SourceDocumentRecord> SourceDocuments,
        IReadOnlyList<DocumentRevisionRecord> DocumentRevisions,
        IReadOnlyList<SubjectCatalogRecord> SubjectCatalogs,
        IReadOnlyList<SubjectAssignmentRecord> SubjectAssignments,
        IReadOnlyList<string> DocumentArtifacts,
        IReadOnlySet<string> CompleteLibraryIds);

    private sealed record DeletionTotals(
        long Pages,
        long Chunks,
        long Profiles,
        long Indexes,
        long Bm25Shards,
        long ExcludedSymbols,
        long AuditEntries,
        long DirectoryLibraries,
        long SourceDocuments,
        long DocumentRevisions,
        long SubjectCatalogs,
        long SubjectAssignments,
        long DocumentArtifacts);

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
        var buildingVersions = await libraryRepo.GetVersionsByPublicationStateAsync(
            VersionPublicationState.Building,
            ct);
        foreach(var building in buildingVersions)
            parents.Add(new LibraryVersionKey(building.LibraryId, building.Version));

        var jobRepo = factory.GetJobRepository(profile);
        var runningScrapes = await jobRepo.ListRunningAsync(JobType.Scrape, ct);
        foreach(var job in runningScrapes)
        {
            if (!string.IsNullOrEmpty(job.LibraryId) && !string.IsNullOrEmpty(job.Version))
                parents.Add(new LibraryVersionKey(job.LibraryId, job.Version));
        }

        var runningDirectoryScans = await jobRepo.ListRunningAsync(JobType.DirectoryScan, ct);
        foreach(var job in runningDirectoryScans)
        {
            if (!string.IsNullOrEmpty(job.LibraryId) && !string.IsNullOrEmpty(job.Version))
                parents.Add(new LibraryVersionKey(job.LibraryId, job.Version));
        }

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
        var sourceRepo = factory.GetSourceDocumentRepository(profile);

        var pagePairs = await pageRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var chunkPairs = await chunkRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var profilePairs = await profileRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var indexPairs = await indexRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var shardPairs = await bm25Repo.GetDistinctLibraryVersionPairsAsync(ct);
        var excludedPairs = await excludedRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var auditPairs = await auditRepo.GetDistinctLibraryVersionPairsAsync(ct);
        var documentRevisionPairs = await sourceRepo.GetDistinctLibraryVersionPairsAsync(ct);

        IReadOnlyList<LibraryVersionKey> orphanPages = FilterOrphans(pagePairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanChunks = FilterOrphans(chunkPairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanProfiles = FilterOrphans(profilePairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanIndexes = FilterOrphans(indexPairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanShards = FilterOrphans(shardPairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanExcluded = FilterOrphans(excludedPairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanAudit = FilterOrphans(auditPairs, parents, library, version);
        IReadOnlyList<LibraryVersionKey> orphanPairs = orphanPages
                                                       .Concat(orphanChunks)
                                                       .Concat(orphanProfiles)
                                                       .Concat(orphanIndexes)
                                                       .Concat(orphanShards)
                                                       .Concat(orphanExcluded)
                                                       .Concat(orphanAudit)
                                                       .Concat(FilterOrphans(documentRevisionPairs,
                                                                             parents,
                                                                             library,
                                                                             version))
                                                       .Distinct()
                                                       .ToList();
        DirectoryOrphanReport documentOrphans = await CollectDocumentOrphansAsync(factory,
                                                                                    profile,
                                                                                    orphanPairs,
                                                                                    ct);
        var report = new OrphanReport(orphanPages,
                                      orphanChunks,
                                      orphanProfiles,
                                      orphanIndexes,
                                      orphanShards,
                                      orphanExcluded,
                                      orphanAudit,
                                      documentOrphans);
        return report;
    }

    private static async Task<DirectoryOrphanReport> CollectDocumentOrphansAsync(
        RepositoryFactory factory,
        string? profile,
        IReadOnlyList<LibraryVersionKey> orphanPairs,
        CancellationToken ct)
    {
        ISourceDocumentRepository sources = factory.GetSourceDocumentRepository(profile);
        ISubjectAssignmentRepository assignments = factory.GetSubjectAssignmentRepository(profile);
        ISubjectCatalogRepository catalogs = factory.GetSubjectCatalogRepository(profile);
        var directoryLibraries = new List<DirectoryLibraryDefinition>();
        var sourceDocuments = new List<SourceDocumentRecord>();
        var revisions = new List<DocumentRevisionRecord>();
        var subjectCatalogs = new List<SubjectCatalogRecord>();
        var subjectAssignments = new List<SubjectAssignmentRecord>();
        var artifactHashes = new HashSet<string>(StringComparer.Ordinal);
        var completeLibraryIds = new HashSet<string>(StringComparer.Ordinal);

        foreach(IGrouping<string, LibraryVersionKey> libraryPairs in orphanPairs.GroupBy(pair => pair.LibraryId,
                                                                                          StringComparer.Ordinal))
        {
            var deletingRevisions = new List<DocumentRevisionRecord>();
            foreach(var pair in libraryPairs)
            {
                IReadOnlyList<DocumentRevisionRecord> pairRevisions =
                    await sources.GetRevisionsAsync(pair.LibraryId, pair.Version, ct);
                deletingRevisions.AddRange(pairRevisions);
            }

            IReadOnlyList<DocumentRevisionRecord> allRevisions = await sources.GetRevisionsAsync(libraryPairs.Key,
                                                                                                   ct);
            var deletingIds = deletingRevisions.Select(revision => revision.Id)
                                               .ToHashSet(StringComparer.Ordinal);
            bool completeLibrary = allRevisions.Count > 0 &&
                                   allRevisions.All(revision => deletingIds.Contains(revision.Id));
            if (completeLibrary)
                completeLibraryIds.Add(libraryPairs.Key);

            IReadOnlyList<SubjectAssignmentRecord> deletingAssignments =
                await assignments.GetByDocumentRevisionIdsAsync(deletingIds, ct);
            IReadOnlyList<SubjectCatalogKey> catalogKeys = deletingAssignments
                                                           .Select(assignment => new SubjectCatalogKey(
                                                                       assignment.LibraryId,
                                                                       assignment.TaxonomyVersion))
                                                           .Distinct()
                                                           .ToList();
            IReadOnlyList<SubjectCatalogRecord> deletingCatalogs = completeLibrary
                                                                        ? await catalogs.GetManyAsync(catalogKeys, ct)
                                                                        : [];
            IReadOnlyList<string> deletingArtifacts =
                await sources.GetArtifactHashesBecomingUnreferencedAsync(deletingIds, ct);
            var documentIds = deletingRevisions.Select(revision => revision.DocumentId)
                                               .Distinct(StringComparer.Ordinal);
            foreach(string documentId in documentIds)
            {
                SourceDocumentRecord? document = await GetOrphanedSourceDocumentAsync(sources,
                                                                                       documentId,
                                                                                       allRevisions,
                                                                                       deletingIds,
                                                                                       ct);
                if (document != null)
                    sourceDocuments.Add(document);
            }

            if (completeLibrary)
            {
                DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryPairs.Key,
                                                                                                      ct);
                if (definition != null)
                    directoryLibraries.Add(definition);
            }

            revisions.AddRange(deletingRevisions);
            subjectAssignments.AddRange(deletingAssignments);
            subjectCatalogs.AddRange(deletingCatalogs);
            artifactHashes.UnionWith(deletingArtifacts);
        }

        var result = new DirectoryOrphanReport(directoryLibraries,
                                               sourceDocuments.DistinctBy(document => document.Id).ToList(),
                                               revisions.DistinctBy(revision => revision.Id).ToList(),
                                               subjectCatalogs.DistinctBy(catalog => catalog.Id).ToList(),
                                               subjectAssignments.DistinctBy(assignment => assignment.Id).ToList(),
                                               artifactHashes.OrderBy(hash => hash, StringComparer.Ordinal).ToList(),
                                               completeLibraryIds);
        return result;
    }

    private static async Task<SourceDocumentRecord?> GetOrphanedSourceDocumentAsync(
        ISourceDocumentRepository sources,
        string documentId,
        IReadOnlyList<DocumentRevisionRecord> allRevisions,
        IReadOnlySet<string> deletingIds,
        CancellationToken ct)
    {
        bool hasSurvivingRevision = allRevisions.Any(revision =>
            string.Equals(revision.DocumentId, documentId, StringComparison.Ordinal) &&
            !deletingIds.Contains(revision.Id));
        SourceDocumentRecord? result = null;
        if (!hasSurvivingRevision)
            result = await sources.GetDocumentAsync(documentId, ct);
        return result;
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
        var sourceRepo = factory.GetSourceDocumentRepository(profile);
        var catalogRepo = factory.GetSubjectCatalogRepository(profile);
        var assignmentRepo = factory.GetSubjectAssignmentRepository(profile);

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
        long documentRevisions = 0;
        long subjectCatalogs = 0;
        long subjectAssignments = 0;
        foreach(IGrouping<string, DocumentRevisionRecord> libraryRevisions in
                orphans.Documents.DocumentRevisions.GroupBy(revision => revision.LibraryId,
                                                             StringComparer.Ordinal))
        {
            if (orphans.Documents.CompleteLibraryIds.Contains(libraryRevisions.Key))
            {
                subjectAssignments += await assignmentRepo.DeleteLibraryAsync(libraryRevisions.Key, ct);
                subjectCatalogs += await catalogRepo.DeleteLibraryAsync(libraryRevisions.Key, ct);
                documentRevisions += await sourceRepo.DeleteLibraryAsync(libraryRevisions.Key, ct);
            }
            else
            {
                IReadOnlyList<string> scanRunIds = libraryRevisions
                                                    .Select(revision => revision.ScanRunId)
                                                    .Distinct(StringComparer.Ordinal)
                                                    .ToList();
                foreach(string scanRunId in scanRunIds)
                {
                    subjectAssignments += await assignmentRepo.DeleteScanRunAsync(libraryRevisions.Key,
                                                                                    scanRunId,
                                                                                    ct);
                    string? deletingVersion = libraryRevisions
                                               .FirstOrDefault(revision =>
                                                   string.Equals(revision.ScanRunId,
                                                                 scanRunId,
                                                                 StringComparison.Ordinal))
                                               ?.Version;
                    subjectCatalogs += await catalogRepo.DeleteCandidateScanRunAsync(libraryRevisions.Key,
                                                                                      scanRunId,
                                                                                      deletingVersion,
                                                                                      ct);
                }

                IReadOnlyList<LibraryVersionKey> revisionPairs = libraryRevisions
                                                                  .Select(revision => new LibraryVersionKey(
                                                                              revision.LibraryId,
                                                                              revision.Version))
                                                                  .Distinct()
                                                                  .ToList();
                documentRevisions += await DeletePerKeyAsync(revisionPairs,
                                                             (key, jobCt) => sourceRepo.DeleteVersionAsync(
                                                                 key.LibraryId,
                                                                 key.Version,
                                                                 jobCt),
                                                             ct);
            }
        }

        var totals = new DeletionTotals(pages,
                                        chunks,
                                        profiles,
                                        indexes,
                                        shards,
                                        excluded,
                                        audit,
                                        orphans.Documents.DirectoryLibraries.Count,
                                        orphans.Documents.SourceDocuments.Count,
                                        documentRevisions,
                                        subjectCatalogs,
                                        subjectAssignments,
                                        orphans.Documents.DocumentArtifacts.Count
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
                             .Concat(orphans.Documents.DocumentRevisions.Select(revision =>
                                 new LibraryVersionKey(revision.LibraryId, revision.Version)))
                             .Concat(orphans.Documents.SubjectAssignments.Select(assignment =>
                                 new LibraryVersionKey(assignment.LibraryId, assignment.Version)))
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
                                                     AuditEntries = orphans.AuditLog.Count,
                                                     DirectoryLibraries = orphans.Documents.DirectoryLibraries.Count,
                                                     SourceDocuments = orphans.Documents.SourceDocuments.Count,
                                                     DocumentRevisions = orphans.Documents.DocumentRevisions.Count,
                                                     SubjectCatalogs = orphans.Documents.SubjectCatalogs.Count,
                                                     SubjectAssignments = orphans.Documents.SubjectAssignments.Count,
                                                     DocumentArtifacts = orphans.Documents.DocumentArtifacts.Count
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
                                                                   HasAuditEntries = orphans.AuditLog.Contains(k),
                                                                   HasDocumentRevisions =
                                                                       orphans.Documents.DocumentRevisions.Any(
                                                                           revision =>
                                                                               string.Equals(revision.LibraryId,
                                                                                             k.LibraryId,
                                                                                             StringComparison.Ordinal) &&
                                                                               string.Equals(revision.Version,
                                                                                             k.Version,
                                                                                             StringComparison.Ordinal)),
                                                                   HasSubjectAssignments =
                                                                       orphans.Documents.SubjectAssignments.Any(
                                                                           assignment =>
                                                                               string.Equals(assignment.LibraryId,
                                                                                             k.LibraryId,
                                                                                             StringComparison.Ordinal) &&
                                                                               string.Equals(assignment.Version,
                                                                                             k.Version,
                                                                                             StringComparison.Ordinal))
                                                               }
                                                    )
                                             .ToList()
                          };
        return summary;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
