// MutationTools.cs
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
///     MCP tools that mutate library state — rename, delete library,
///     delete version. All default to dryRun=true so the calling LLM
///     can preview the cascade before committing. Apply paths (dryRun=false)
///     queue a background job and return a JobId immediately.
/// </summary>
[McpServerToolType]
public static class MutationTools
{
    [McpServerTool(Name = "rename_library")]
    [Description("Rename a library OR a single version. Library mode: pass 'newId' to rename the " +
                 "library across every collection. Version mode: pass 'version' + 'newVersion' to " +
                 "relabel one version (e.g. current -> v8), repointing CurrentVersion if needed. " +
                 "Exactly one mode per call. Defaults to dryRun=true; dryRun=false queues a background " +
                 "job and returns { JobId, Status: 'Queued' }. Outcome=Collision if the target id/version " +
                 "already exists; never merges.")]
    public static async Task<string> RenameLibrary(RepositoryFactory repositoryFactory,
                                                   [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                   IBackgroundJobRunner runner,
                                                   ILibraryRenameService renameService,
                                                   [Description("Current library identifier")]
                                                   string library,
                                                   [Description("New library identifier (library-rename mode)")]
                                                   string? newId = null,
                                                   [Description("Existing version to rename (version-rename mode)")]
                                                   string? version = null,
                                                   [Description("New version label (version-rename mode)")]
                                                   string? newVersion = null,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(renameService);
        ArgumentException.ThrowIfNullOrEmpty(library);

        string? modeError = ValidateRenameMode(newId, version, newVersion);
        string ver = version ?? string.Empty;
        string newVer = newVersion ?? string.Empty;
        Task<string> dispatch = modeError switch
            {
                not null => Task.FromResult(JsonSerializer.Serialize(new { Error = modeError }, smJsonOptions)),
                var _ when !string.IsNullOrEmpty(newId)
                    => RunLibraryRenameAsync(repositoryFactory,
                                             runner,
                                             renameService,
                                             library,
                                             newId,
                                             dryRun,
                                             profile,
                                             ct),
                var _ => RunVersionRenameAsync(repositoryFactory,
                                               runner,
                                               renameService,
                                               library,
                                               ver,
                                               newVer,
                                               dryRun,
                                               profile,
                                               ct)
            };
        string result = await dispatch;
        return result;
    }

    private static string? ValidateRenameMode(string? newId, string? version, string? newVersion)
    {
        bool wantsLibrary = !string.IsNullOrEmpty(newId);
        bool wantsVersion = !string.IsNullOrEmpty(version) || !string.IsNullOrEmpty(newVersion);
        bool newIdHasSlash = newId != null && newId.Contains(SlashString, StringComparison.Ordinal);
        bool newVersionHasSlash = newVersion != null && newVersion.Contains(SlashString, StringComparison.Ordinal);

        string? error = (wantsLibrary, wantsVersion) switch
            {
                (true, true)   => BothModesError,
                (false, false) => NothingToRenameError,
                (true, false) when newIdHasSlash => NewIdSlashError,
                (false, true) when string.IsNullOrEmpty(version) || string.IsNullOrEmpty(newVersion)
                               => VersionBothRequiredError,
                (false, true) when string.Equals(version, newVersion, StringComparison.Ordinal)
                               => NewVersionSameError,
                (false, true) when newVersionHasSlash => NewVersionSlashError,
                var _ => null
            };
        return error;
    }

    private static async Task<string> RunLibraryRenameAsync(RepositoryFactory repositoryFactory,
                                                            IBackgroundJobRunner runner,
                                                            ILibraryRenameService renameService,
                                                            string library, string newId,
                                                            bool dryRun, string? profile, CancellationToken ct)
    {
        string result;
        if (dryRun)
        {
            var preview = await PreviewRenameAsync(repositoryFactory, profile, library, newId, ct);
            object? wouldRename = preview.counts;
            if (preview is { outcome: RenameLibraryOutcome.Renamed, counts: not null })
            {
                ILibraryRepository libraryRepo = repositoryFactory.GetLibraryRepository(profile);
                LibraryRecord? existing = await libraryRepo.GetLibraryAsync(library, ct);
                if (existing != null)
                {
                    var documents = await GetDocumentDeletionPreviewAsync(repositoryFactory,
                                                                           profile,
                                                                           library,
                                                                           existing.AllVersions,
                                                                           ct);
                    wouldRename = new
                                      {
                                          preview.counts.Libraries,
                                          preview.counts.Versions,
                                          preview.counts.Chunks,
                                          preview.counts.Pages,
                                          preview.counts.Profiles,
                                          preview.counts.Indexes,
                                          preview.counts.Bm25Shards,
                                          preview.counts.ExcludedSymbols,
                                          preview.counts.ScrapeJobs,
                                          documents.DirectoryLibraries,
                                          documents.SourceDocuments,
                                          documents.DocumentRevisions,
                                          documents.SubjectCatalogs,
                                          documents.SubjectAssignments,
                                          documents.ArtifactReferences
                                      };
                }
            }

            var response = new { DryRun = true, Outcome = preview.outcome.ToString(), WouldRename = wouldRename };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }
        else
        {
            result = await QueueRenameLibraryJobAsync(library,
                                                      newId,
                                                      renameService,
                                                      runner,
                                                      profile,
                                                      ct);
        }

        return result;
    }

    private static async Task<string> RunVersionRenameAsync(RepositoryFactory repositoryFactory,
                                                            IBackgroundJobRunner runner,
                                                            ILibraryRenameService renameService,
                                                            string library, string version, string newVersion,
                                                            bool dryRun, string? profile, CancellationToken ct)
    {
        string result;
        if (dryRun)
        {
            var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
            var chunkRepo = repositoryFactory.GetChunkRepository(profile);
            var pageRepo = repositoryFactory.GetPageRepository(profile);

            var existing = await libraryRepo.GetVersionAsync(library, version, ct);
            var collision = await libraryRepo.GetVersionAsync(library, newVersion, ct);
            var lib = await libraryRepo.GetLibraryAsync(library, ct);

            var outcome = existing is null ? RenameLibraryOutcome.NotFound
                        : collision is not null ? RenameLibraryOutcome.Collision
                        : RenameLibraryOutcome.Renamed;

            object? wouldRename = null;
            if (outcome == RenameLibraryOutcome.Renamed)
            {
                var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
                var pages = await pageRepo.GetPageCountAsync(library, version, ct);
                var documents = await GetDocumentDeletionPreviewAsync(repositoryFactory,
                                                                       profile,
                                                                       library,
                                                                       [version],
                                                                       ct);
                string? repoint = lib?.CurrentVersion == version ? newVersion : null;
                wouldRename = new
                                  {
                                      Versions = 1,
                                      Chunks = chunks,
                                      Pages = pages,
                                      CurrentVersionRepointedTo = repoint,
                                      documents.DirectoryLibraries,
                                      documents.SourceDocuments,
                                      documents.DocumentRevisions,
                                      documents.SubjectCatalogs,
                                      documents.SubjectAssignments,
                                      documents.ArtifactReferences
                                  };
            }

            var response = new { DryRun = true, Outcome = outcome.ToString(), WouldRename = wouldRename };
            result = JsonSerializer.Serialize(response, smJsonOptions);
        }
        else
        {
            result = await QueueRenameVersionJobAsync(library,
                                                      version,
                                                      newVersion,
                                                      renameService,
                                                      runner,
                                                      profile,
                                                      ct);
        }

        return result;
    }

    private static async Task<string> QueueRenameVersionJobAsync(string library,
                                                                 string version,
                                                                 string newVersion,
                                                                 ILibraryRenameService renameService,
                                                                 IBackgroundJobRunner runner,
                                                                 string? profile,
                                                                 CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { library, version, newVersion, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.RenameVersion,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, _, jobCt) =>
                                            {
                                                var renameResult =
                                                    await renameService.RenameVersionAsync(profile,
                                                                                           library,
                                                                                           version,
                                                                                           newVersion,
                                                                                           jobCt);
                                                record.ResultJson = JsonSerializer.Serialize(new
                                                             {
                                                                 DryRun = false,
                                                                 Outcome = renameResult.Outcome.ToString(),
                                                                 renameResult.Counts,
                                                                 renameResult.Warning
                                                             }, smJsonOptions);
                                            },
                                            ct
                                           );

        return JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) }, smJsonOptions);
    }

    private static async Task<string> QueueRenameLibraryJobAsync(string library,
                                                                 string newId,
                                                                 ILibraryRenameService renameService,
                                                                 IBackgroundJobRunner runner,
                                                                 string? profile,
                                                                 CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { library, newId, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.RenameLibrary,
                                Profile = profile,
                                LibraryId = library,
                                InputJson = inputJson
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, _, jobCt) =>
                                            {
                                                var renameResult = await renameService.RenameLibraryAsync(profile,
                                                                                                           library,
                                                                                                           newId,
                                                                                                           jobCt);
                                                record.ResultJson = JsonSerializer.Serialize(new
                                                             {
                                                         DryRun = false,
                                                         Outcome = renameResult.Outcome.ToString(),
                                                         renameResult.Counts,
                                                         renameResult.Warning
                                                             },
                                                         smJsonOptions
                                                    );
                                            },
                                            ct
                                           );

        return JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) }, smJsonOptions);
    }

    private static async Task<(RenameLibraryOutcome outcome, RenameLibraryResult? counts)> PreviewRenameAsync(
        RepositoryFactory factory,
        string? profile,
        string oldId,
        string newId,
        CancellationToken ct)
    {
        var libraryRepo = factory.GetLibraryRepository(profile);
        var existing = await libraryRepo.GetLibraryAsync(oldId, ct);
        var collision = await libraryRepo.GetLibraryAsync(newId, ct);

        var result = existing switch
            {
                null => (RenameLibraryOutcome.NotFound, null),
                var _ when collision != null => (RenameLibraryOutcome.Collision, null),
                var _ => MakeSuccessResult(existing)
            };

        return result;
    }

    private static (RenameLibraryOutcome outcome, RenameLibraryResult counts)
        MakeSuccessResult(LibraryRecord existing) =>
        (
            RenameLibraryOutcome.Renamed,
            new RenameLibraryResult(Libraries: 1,
                                    existing.AllVersions.Count,
                                    Chunks: 0,
                                    Pages: 0,
                                    Profiles: 0,
                                    Indexes: 0,
                                    Bm25Shards: 0,
                                    ExcludedSymbols: 0,
                                    ScrapeJobs: 0
                                   )
        );

    [McpServerTool(Name = "delete_version")]
    [Description("Hard-delete one (library, version): chunks, pages, profile, indexes, " +
                 "bm25 shards, excluded symbols, and the LibraryVersions row. Cascade-deletes " +
                 "the parent Library row if no other versions remain. ScrapeJobs are retained " +
                 "for audit. Defaults to dryRun=true. dryRun=false returns { JobId, Status: 'Queued' } " +
                 "immediately; poll get_job_status for the outcome."
                )]
    public static async Task<string> DeleteVersion(RepositoryFactory repositoryFactory,
                                                   [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                   IBackgroundJobRunner runner,
                                                   ILibraryDeletionService deletionService,
                                                   [Description("Library identifier")] string library,
                                                   [Description("Version to delete")] string version,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        string result;
        if (dryRun)
        {
            var chunkRepo = repositoryFactory.GetChunkRepository(profile);
            var pageRepo = repositoryFactory.GetPageRepository(profile);
            var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

            var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
            var pages = await pageRepo.GetPageCountAsync(library, version, ct);
            var lib = await libraryRepo.GetLibraryAsync(library, ct);
            var documents = await GetDocumentDeletionPreviewAsync(repositoryFactory,
                                                                   profile,
                                                                   library,
                                                                   [version],
                                                                   ct);
            bool wouldDeleteLibraryRow =
                lib is { AllVersions.Count: 1 } && lib.AllVersions[index: 0] == version;
            string? wouldRepointTo = lib != null && lib.CurrentVersion == version && lib.AllVersions.Count > 1
                                         ? lib.AllVersions.First(v => v != version)
                                         : null;

            var preview = new
                              {
                                  DryRun = true,
                                  WouldDelete = new
                                                    {
                                                        Versions = new[] { version },
                                                        Chunks = chunks,
                                                        Pages = pages,
                                                        Profiles = 1,
                                                        Indexes = 1,
                                                         Bm25Shards = 1,
                                                         ExcludedSymbols = 1,
                                                         documents.DirectoryLibraries,
                                                         documents.SourceDocuments,
                                                         documents.DocumentRevisions,
                                                         documents.SubjectCatalogs,
                                                         documents.SubjectAssignments,
                                                         documents.ArtifactReferences
                                                    },
                                  LibraryRowAffected = wouldDeleteLibraryRow,
                                  CurrentVersionRepointedTo = wouldRepointTo,
                                  ScrapeJobsRetained = PreservedForAudit
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
        {
            result = await QueueDeleteVersionJobAsync(library,
                                                      version,
                                                      deletionService,
                                                      runner,
                                                      profile,
                                                      ct
                                                     );
        }

        return result;
    }

    private static async Task<string> QueueDeleteVersionJobAsync(string library,
                                                                 string version,
                                                                 ILibraryDeletionService deletionService,
                                                                 IBackgroundJobRunner runner,
                                                                 string? profile,
                                                                 CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { library, version, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.DeleteVersion,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, _, jobCt) =>
                                            {
                                                var deleted = await deletionService.DeleteVersionAsync(profile,
                                                     library,
                                                     version,
                                                     jobCt
                                                );

                                                record.ResultJson = JsonSerializer.Serialize(new
                                                             {
                                                                 DryRun = false,
                                                                 Deleted = deleted,
                                                                 LibraryRowDeleted = deleted.Libraries > 0,
                                                                 deleted.CurrentVersionRepointedTo
                                                             },
                                                         smJsonOptions
                                                    );
                                            },
                                            ct
                                           );

        return JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) }, smJsonOptions);
    }

    [McpServerTool(Name = "delete_library")]
    [Description("Hard-delete an entire library across every collection except ScrapeJobs " +
                 "(retained for audit). Cascades through every version. Defaults to " +
                 "dryRun=true so the calling LLM can preview the cascade before applying. " +
                 "dryRun=false returns { JobId, Status: 'Queued' } immediately; poll get_job_status for the outcome."
                )]
    public static async Task<string> DeleteLibrary(RepositoryFactory repositoryFactory,
                                                   [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                   IBackgroundJobRunner runner,
                                                   ILibraryDeletionService deletionService,
                                                   [Description("Library identifier")] string library,
                                                   [Description("If true (default), preview without writing.")]
                                                   bool dryRun = true,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var lib = await libraryRepo.GetLibraryAsync(library, ct);

        string result = lib switch
            {
                null => JsonSerializer.Serialize(new { Status = NotFoundStatus, Library = library }, smJsonOptions),
                var _ when dryRun => await GetDeleteLibraryDryRunResultAsync(library,
                                                                             lib,
                                                                             repositoryFactory,
                                                                             profile,
                                                                             ct
                                                                            ),
                var _ => await QueueDeleteLibraryJobAsync(library, deletionService, runner, profile, ct)
            };

        return result;
    }

    private static async Task<string> QueueDeleteLibraryJobAsync(string library,
                                                                 ILibraryDeletionService deletionService,
                                                                 IBackgroundJobRunner runner,
                                                                 string? profile,
                                                                 CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { library, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.DeleteLibrary,
                                Profile = profile,
                                LibraryId = library,
                                InputJson = inputJson
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, _, jobCt) =>
                                            {
                                                var deleted = await deletionService.DeleteLibraryAsync(profile,
                                                     library,
                                                     jobCt
                                                );
                                                record.ResultJson = JsonSerializer.Serialize(new
                                                     {
                                                         DryRun = false,
                                                         Deleted = deleted
                                                     },
                                                     smJsonOptions
                                                );
                                            },
                                            ct
                                           );

        return JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) }, smJsonOptions);
    }

    private static async Task<string> GetDeleteLibraryDryRunResultAsync(string library,
                                                                        LibraryRecord lib,
                                                                        RepositoryFactory repositoryFactory,
                                                                        string? profile,
                                                                        CancellationToken ct)
    {
        IChunkRepository chunkRepo = repositoryFactory.GetChunkRepository(profile);
        IPageRepository pageRepo = repositoryFactory.GetPageRepository(profile);
        long totalChunks = 0;
        long totalPages = 0;
        foreach(var v in lib.AllVersions)
        {
            totalChunks += await chunkRepo.GetChunkCountAsync(library, v, ct);
            totalPages += await pageRepo.GetPageCountAsync(library, v, ct);
        }

        var documents = await GetDocumentDeletionPreviewAsync(repositoryFactory,
                                                               profile,
                                                               library,
                                                               lib.AllVersions,
                                                               ct);

        var preview = new
                          {
                              DryRun = true,
                              WouldDelete = new
                                                {
                                                    Library = 1,
                                                    Versions = lib.AllVersions.ToArray(),
                                                    Chunks = totalChunks,
                                                    Pages = totalPages,
                                                    Profiles = lib.AllVersions.Count,
                                                    Indexes = lib.AllVersions.Count,
                                                     Bm25Shards = lib.AllVersions.Count,
                                                     ExcludedSymbols = lib.AllVersions.Count,
                                                     documents.DirectoryLibraries,
                                                     documents.SourceDocuments,
                                                     documents.DocumentRevisions,
                                                     documents.SubjectCatalogs,
                                                     documents.SubjectAssignments,
                                                     documents.ArtifactReferences
                                                 },
                              ScrapeJobsRetained = PreservedForAudit
                          };
        return JsonSerializer.Serialize(preview, smJsonOptions);
    }

    private static async Task<(int DirectoryLibraries,
                               int SourceDocuments,
                               int DocumentRevisions,
                               int SubjectCatalogs,
                               int SubjectAssignments,
                               int ArtifactReferences)> GetDocumentDeletionPreviewAsync(
        RepositoryFactory repositoryFactory,
        string? profile,
        string library,
        IReadOnlyList<string> versions,
        CancellationToken ct)
    {
        ISourceDocumentRepository sources = repositoryFactory.GetSourceDocumentRepository(profile);
        ISubjectAssignmentRepository assignments = repositoryFactory.GetSubjectAssignmentRepository(profile);
        ISubjectCatalogRepository catalogs = repositoryFactory.GetSubjectCatalogRepository(profile);
        var revisions = new List<DocumentRevisionRecord>();
        foreach(string version in versions)
        {
            IReadOnlyList<DocumentRevisionRecord> versionRevisions =
                await sources.GetRevisionsAsync(library, version, ct);
            revisions.AddRange(versionRevisions);
        }

        IReadOnlyList<string> revisionIds = revisions.Select(revision => revision.Id)
                                                      .Distinct(StringComparer.Ordinal)
                                                      .ToList();
        IReadOnlyList<SubjectAssignmentRecord> subjectAssignments =
            await assignments.GetByDocumentRevisionIdsAsync(revisionIds, ct);
        IReadOnlyList<SubjectCatalogKey> catalogKeys = subjectAssignments
                                                       .Select(assignment => new SubjectCatalogKey(
                                                                   assignment.LibraryId,
                                                                   assignment.TaxonomyVersion))
                                                       .Distinct()
                                                       .ToList();
        IReadOnlyList<SubjectCatalogRecord> subjectCatalogs = await catalogs.GetManyAsync(catalogKeys, ct);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(library, ct);
        int sourceDocuments = revisions.Select(revision => revision.DocumentId)
                                       .Distinct(StringComparer.Ordinal)
                                       .Count();
        int artifactReferences = revisions.Sum(revision => revision.ExtractionArtifactHash == null ? 1 : 2);
        var result = (DirectoryLibraries: definition == null ? 0 : 1,
                      SourceDocuments: sourceDocuments,
                      DocumentRevisions: revisions.Count,
                      SubjectCatalogs: subjectCatalogs.Count,
                      SubjectAssignments: subjectAssignments.Count,
                      ArtifactReferences: artifactReferences);
        return result;
    }

    private const string PreservedForAudit = "preserved for audit";
    private const string NotFoundStatus = "NotFound";
    private const string SlashString = "/";
    private const string BothModesError =
        "Specify a library rename ('newId') OR a version rename ('version' + 'newVersion'), not both.";
    private const string NothingToRenameError =
        "Nothing to rename: provide 'newId', or both 'version' and 'newVersion'.";
    private const string NewIdSlashError = "'newId' must not contain '/'.";
    private const string VersionBothRequiredError =
        "A version rename requires both 'version' and 'newVersion'.";
    private const string NewVersionSameError = "'newVersion' must differ from 'version'.";
    private const string NewVersionSlashError = "'newVersion' must not contain '/'.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
