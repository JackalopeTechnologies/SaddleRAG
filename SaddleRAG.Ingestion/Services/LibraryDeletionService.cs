// LibraryDeletionService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.ExceptionServices;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Ingestion.Services;

/// <summary>
///     Shared deletion cascade used by explicit Monitor and MCP operations.
///     Version metadata is removed only after every owned store has reached
///     its requested cleanup boundary.
/// </summary>
public sealed class LibraryDeletionService : ILibraryDeletionService
{
    public LibraryDeletionService(RepositoryFactory repositoryFactory, IVectorSearchProvider vectorSearch)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        mRepositoryFactory = repositoryFactory;
        mVectorSearch = vectorSearch;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IVectorSearchProvider mVectorSearch;

    public async Task<LibraryDeletionResult> DeleteVersionAsync(string? profile,
                                                                string libraryId,
                                                                string version,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var repositories = GetRepositories(profile);
        var attempts = new DeletionAttempts(ct);
        LibraryRecord? library = await attempts.AttemptAsync(
            token => repositories.Libraries.GetLibraryAsync(libraryId, token),
            fallback: null);
        bool deletesLibrary = library is { AllVersions.Count: 1 } &&
                              string.Equals(library.AllVersions[index: 0], version, StringComparison.Ordinal);
        var counts = await DeleteVersionStoresAsync(repositories,
                                                     libraryId,
                                                     version,
                                                     attempts);
        if (deletesLibrary)
        {
            long remainingDocumentRevisions = await attempts.AttemptAsync(
                token => repositories.SourceDocuments.DeleteLibraryAsync(libraryId, token),
                fallback: 0L);
            long remainingSubjectAssignments = await attempts.AttemptAsync(
                token => repositories.SubjectAssignments.DeleteLibraryAsync(libraryId, token),
                fallback: 0L);
            long remainingSubjectCatalogs = await attempts.AttemptAsync(
                token => repositories.SubjectCatalogs.DeleteLibraryAsync(libraryId, token),
                fallback: 0L);
            counts = counts with
                         {
                             DocumentRevisions = counts.DocumentRevisions + remainingDocumentRevisions,
                             SubjectAssignments = counts.SubjectAssignments + remainingSubjectAssignments,
                             SubjectCatalogs = counts.SubjectCatalogs + remainingSubjectCatalogs
                         };
        }

        await attempts.AttemptAsync(token => mVectorSearch.RemoveIndexAsync(profile,
                                                                             libraryId,
                                                                             version,
                                                                             token));
        attempts.ThrowIfFailed();

        var metadata = await repositories.Libraries.DeleteVersionAsync(libraryId, version, ct);
        return CreateResult(metadata, counts);
    }

    public async Task<LibraryDeletionResult> DeleteLibraryAsync(string? profile,
                                                                string libraryId,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var repositories = GetRepositories(profile);
        var attempts = new DeletionAttempts(ct);
        var library = await attempts.AttemptAsync(token => repositories.Libraries.GetLibraryAsync(libraryId,
                                                                                                   token),
                                                  fallback: null);
        var versions = await attempts.AttemptAsync(token => repositories.Libraries.GetVersionsAsync(libraryId,
                                                                                                      token),
                                                   Array.Empty<LibraryVersionRecord>());
        var counts = StoreDeletionCounts.Empty;
        foreach(var version in versions)
        {
            var versionCounts = await DeleteVersionStoresAsync(repositories,
                                                                libraryId,
                                                                version.Version,
                                                                attempts);
            counts = counts.Add(versionCounts);
        }

        var remainingDocumentRevisions = await attempts.AttemptAsync(
            token => repositories.SourceDocuments.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        var remainingSubjectAssignments = await attempts.AttemptAsync(
            token => repositories.SubjectAssignments.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        var remainingSubjectCatalogs = await attempts.AttemptAsync(
            token => repositories.SubjectCatalogs.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        counts = counts with
                     {
                         DocumentRevisions = counts.DocumentRevisions + remainingDocumentRevisions,
                         SubjectAssignments = counts.SubjectAssignments + remainingSubjectAssignments,
                         SubjectCatalogs = counts.SubjectCatalogs + remainingSubjectCatalogs
                     };
        await attempts.AttemptAsync(token => mVectorSearch.RemoveLibraryIndexesAsync(profile,
                                                                                      libraryId,
                                                                                      token));
        attempts.ThrowIfFailed();

        var versionsDeleted = await repositories.Libraries.DeleteAsync(libraryId, ct);
        return new LibraryDeletionResult(library == null ? 0 : 1,
                                         versionsDeleted,
                                         counts.Chunks,
                                         counts.Pages,
                                         counts.Profiles,
                                         counts.Indexes,
                                         counts.Bm25Shards,
                                         counts.ExcludedSymbols,
                                         counts.AuditEntries,
                                         CurrentVersionRepointedTo: null,
                                         DocumentRevisions: counts.DocumentRevisions,
                                         SubjectAssignments: counts.SubjectAssignments,
                                         SubjectCatalogs: counts.SubjectCatalogs);
    }

    private async Task<StoreDeletionCounts> DeleteVersionStoresAsync(DeletionRepositories repositories,
                                                                      string libraryId,
                                                                      string version,
                                                                      DeletionAttempts attempts)
    {
        IReadOnlyList<DocumentRevisionRecord> revisions = await attempts.AttemptAsync(
            token => repositories.SourceDocuments.GetRevisionsAsync(libraryId, version, token),
            Array.Empty<DocumentRevisionRecord>());
        IReadOnlyList<string> scanRunIds = revisions
                                           .Select(revision => revision.ScanRunId)
                                           .Where(scanRunId => !string.IsNullOrWhiteSpace(scanRunId))
                                           .Distinct(StringComparer.Ordinal)
                                           .ToList();
        var chunks = await attempts.AttemptAsync(
            token => repositories.Chunks.DeleteChunksAsync(libraryId, version, token),
            fallback: 0L);
        var pages = await attempts.AttemptAsync(
            token => repositories.Pages.DeleteAsync(libraryId, version, token),
            fallback: 0L);
        var profiles = await attempts.AttemptAsync(
            token => repositories.Profiles.DeleteAsync(libraryId, version, token),
            fallback: 0L);
        var indexes = await attempts.AttemptAsync(
            token => repositories.Indexes.DeleteAsync(libraryId, version, token),
            fallback: 0L);
        var bm25Shards = await attempts.AttemptAsync(
            token => repositories.Bm25Shards.DeleteAsync(libraryId, version, token),
            fallback: 0L);
        var excludedSymbols = await attempts.AttemptAsync(
            token => repositories.ExcludedSymbols.DeleteAsync(libraryId, version, token),
            fallback: 0L);
        var auditEntries = await attempts.AttemptAsync(
            token => repositories.Audit.DeleteByLibraryVersionAsync(libraryId, version, token),
            fallback: 0L);
        var subjectAssignments = 0L;
        var subjectCatalogs = 0L;
        foreach(string scanRunId in scanRunIds)
        {
            subjectAssignments += await attempts.AttemptAsync(
                token => repositories.SubjectAssignments.DeleteScanRunAsync(libraryId, scanRunId, token),
                fallback: 0L);
            subjectCatalogs += await attempts.AttemptAsync(
                token => repositories.SubjectCatalogs.DeleteCandidateScanRunAsync(libraryId,
                                                                                   scanRunId,
                                                                                   version,
                                                                                   token),
                fallback: 0L);
        }

        var documentRevisions = await attempts.AttemptAsync(
            token => repositories.SourceDocuments.DeleteVersionAsync(libraryId, version, token),
            fallback: 0L);
        return new StoreDeletionCounts(chunks,
                                       pages,
                                       profiles,
                                       indexes,
                                       bm25Shards,
                                       excludedSymbols,
                                       auditEntries,
                                       documentRevisions,
                                       subjectAssignments,
                                       subjectCatalogs);
    }

    private DeletionRepositories GetRepositories(string? profile) =>
        new(mRepositoryFactory.GetLibraryRepository(profile),
            mRepositoryFactory.GetChunkRepository(profile),
            mRepositoryFactory.GetPageRepository(profile),
            mRepositoryFactory.GetLibraryProfileRepository(profile),
            mRepositoryFactory.GetLibraryIndexRepository(profile),
            mRepositoryFactory.GetBm25ShardRepository(profile),
            mRepositoryFactory.GetExcludedSymbolsRepository(profile),
            mRepositoryFactory.GetScrapeAuditRepository(profile),
            mRepositoryFactory.GetSourceDocumentRepository(profile),
            mRepositoryFactory.GetSubjectCatalogRepository(profile),
            mRepositoryFactory.GetSubjectAssignmentRepository(profile));

    private static LibraryDeletionResult CreateResult(DeleteVersionResult metadata,
                                                       StoreDeletionCounts counts) =>
        new(metadata.LibraryRowDeleted ? 1 : 0,
            metadata.VersionsDeleted,
            counts.Chunks,
            counts.Pages,
            counts.Profiles,
            counts.Indexes,
            counts.Bm25Shards,
            counts.ExcludedSymbols,
            counts.AuditEntries,
            metadata.CurrentVersionRepointedTo,
            counts.DocumentRevisions,
            counts.SubjectAssignments,
            counts.SubjectCatalogs);

    private sealed record DeletionRepositories(ILibraryRepository Libraries,
                                               IChunkRepository Chunks,
                                               IPageRepository Pages,
                                               ILibraryProfileRepository Profiles,
                                               ILibraryIndexRepository Indexes,
                                               IBm25ShardRepository Bm25Shards,
                                               IExcludedSymbolsRepository ExcludedSymbols,
                                               IScrapeAuditRepository Audit,
                                               ISourceDocumentRepository SourceDocuments,
                                               ISubjectCatalogRepository SubjectCatalogs,
                                               ISubjectAssignmentRepository SubjectAssignments);

    private sealed record StoreDeletionCounts(long Chunks,
                                               long Pages,
                                               long Profiles,
                                               long Indexes,
                                               long Bm25Shards,
                                               long ExcludedSymbols,
                                               long AuditEntries,
                                               long DocumentRevisions,
                                               long SubjectAssignments,
                                               long SubjectCatalogs)
    {
        internal StoreDeletionCounts Add(StoreDeletionCounts other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return new StoreDeletionCounts(Chunks + other.Chunks,
                                           Pages + other.Pages,
                                           Profiles + other.Profiles,
                                           Indexes + other.Indexes,
                                           Bm25Shards + other.Bm25Shards,
                                           ExcludedSymbols + other.ExcludedSymbols,
                                           AuditEntries + other.AuditEntries,
                                           DocumentRevisions + other.DocumentRevisions,
                                           SubjectAssignments + other.SubjectAssignments,
                                           SubjectCatalogs + other.SubjectCatalogs);
        }

        internal static StoreDeletionCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class DeletionAttempts
    {
        internal DeletionAttempts(CancellationToken requestedToken)
        {
            mRequestedToken = requestedToken;
        }

        private ExceptionDispatchInfo? mFailure;
        private readonly CancellationToken mRequestedToken;

        internal async Task<T> AttemptAsync<T>(Func<CancellationToken, Task<T>> operation, T fallback)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var result = fallback;
            try
            {
                result = await operation(CurrentToken);
            }
            catch(Exception ex)
            {
                mFailure ??= ExceptionDispatchInfo.Capture(ex);
            }

            return result;
        }

        internal async Task AttemptAsync(Func<CancellationToken, Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            try
            {
                await operation(CurrentToken);
            }
            catch(Exception ex)
            {
                mFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        internal void ThrowIfFailed()
        {
            mFailure?.Throw();
        }

        private CancellationToken CurrentToken => mFailure == null
                                                      ? mRequestedToken
                                                      : CancellationToken.None;
    }
}
