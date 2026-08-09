// LibraryDeletionService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.ExceptionServices;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Scanning;

#endregion

namespace SaddleRAG.Ingestion.Services;

/// <summary>
///     Shared deletion cascade used by explicit Monitor and MCP operations.
///     Version metadata is removed only after every owned store has reached
///     its requested cleanup boundary.
/// </summary>
public sealed class LibraryDeletionService : ILibraryDeletionService
{
    public LibraryDeletionService(RepositoryFactory repositoryFactory,
                                  IVectorSearchProvider vectorSearch,
                                  ILibraryIngestionModeLeaseManager? modeLeaseManager = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        mRepositoryFactory = repositoryFactory;
        mVectorSearch = vectorSearch;
        mModeLeaseManager = modeLeaseManager ??
                            new LibraryIngestionModeLeaseManager(repositoryFactory, TimeProvider.System);
    }

    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IVectorSearchProvider mVectorSearch;

    public Task<LibraryDeletionResult> DeleteVersionAsync(string? profile,
                                                          string libraryId,
                                                          string version,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return DeleteVersionPublicAsync(profile, libraryId, version, preservedJobId: null, ct);
    }

    public async Task<LibraryDeletionResult> DeleteVersionPreservingJobAsync(
        string? profile,
        string libraryId,
        string version,
        string preservedJobId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(preservedJobId);
        return await DeleteVersionPublicAsync(profile, libraryId, version, preservedJobId, ct);
    }

    private async Task<LibraryDeletionResult> DeleteVersionPublicAsync(string? profile,
                                                                        string libraryId,
                                                                        string version,
                                                                        string? preservedJobId,
                                                                        CancellationToken ct)
    {
        ILibraryIngestionModeLease acquired = await AcquireModeLeaseAsync(profile, libraryId, ct);
        await using ILibraryIngestionModeLease modeLease = acquired;
        LibraryDeletionResult result = await DeleteVersionUnderModeLeaseInternalAsync(profile,
                                                                                        libraryId,
                                                                                        version,
                                                                                        modeLease,
                                                                                        preservedJobId,
                                                                                        ct);
        if (!await HasOwnedContentAsync(profile, libraryId))
        {
            bool deleted = await modeLease.TryDeleteOwnershipAsync(CancellationToken.None);
            if (!deleted)
                throw new InvalidOperationException(ModeLifecycleLostDetail);
        }

        return result;
    }

    public Task<LibraryDeletionResult> DeleteVersionUnderModeLeaseAsync(
        string? profile,
        string libraryId,
        string version,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(modeLease);
        return DeleteVersionUnderModeLeaseInternalAsync(profile,
                                                        libraryId,
                                                        version,
                                                        modeLease,
                                                        preservedJobId: null,
                                                        ct);
    }

    private async Task<LibraryDeletionResult> DeleteVersionUnderModeLeaseInternalAsync(
        string? profile,
        string libraryId,
        string version,
        ILibraryIngestionModeLease modeLease,
        string? preservedJobId,
        CancellationToken ct)
    {
        ValidateModeLease(modeLease, libraryId);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            modeLease.OwnershipLostToken);
        await RequireActiveModeLeaseAsync(modeLease, operation.Token);
        return await DeleteVersionWithPublicationLeaseAsync(profile,
                                                            libraryId,
                                                            version,
                                                            modeLease,
                                                            preservedJobId,
                                                            operation.Token);
    }

    private async Task<LibraryDeletionResult> DeleteVersionWithPublicationLeaseAsync(
        string? profile,
        string libraryId,
        string version,
        ILibraryIngestionModeLease? modeLease,
        string? preservedJobId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryId, ct);
        LibraryDeletionResult result;
        if (definition == null)
        {
            result = await DeleteVersionCoreAsync(profile,
                                                  libraryId,
                                                  version,
                                                  publicationLease: null,
                                                  modeLease,
                                                  preservedJobId,
                                                  ct);
        }
        else
        {
            IDirectoryPublicationLease? publicationLease =
                await sources.TryAcquireDirectoryPublicationLeaseAsync(
                    libraryId,
                    definition.RegistrationRevision,
                    definition.RegistrationIncarnationId,
                    $"delete-version-{Guid.NewGuid():N}",
                    definition.LastPublishedVersion,
                    ct);
            if (publicationLease == null)
                throw new InvalidOperationException(DirectoryLifecycleBusyDetail);

            await using(publicationLease)
            {
                result = await DeleteVersionCoreAsync(profile,
                                                      libraryId,
                                                      version,
                                                      publicationLease,
                                                      modeLease,
                                                      preservedJobId,
                                                      ct);
                if (string.Equals(definition.LastPublishedVersion, version, StringComparison.Ordinal))
                {
                    await UpdateDirectoryPointerAfterVersionDeletionAsync(profile,
                                                                           definition,
                                                                           publicationLease,
                                                                           modeLease,
                                                                           CancellationToken.None);
                }
            }
        }

        return result;
    }

    private async Task UpdateDirectoryPointerAfterVersionDeletionAsync(
        string? profile,
        DirectoryLibraryDefinition definition,
        IDirectoryPublicationLease publicationLease,
        ILibraryIngestionModeLease? modeLease,
        CancellationToken ct)
    {
        await RequireActiveModeLeaseAsync(modeLease, ct);
        await RequireActiveLeaseAsync(publicationLease, ct);
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
        LibraryRecord? remaining = await libraries.GetLibraryAsync(definition.Id, ct);
        string? publishedVersion = remaining?.CurrentVersion;
        DateTime? publishedAtUtc = null;
        if (publishedVersion != null)
        {
            LibraryVersionRecord? version = await libraries.GetVersionAsync(definition.Id,
                                                                              publishedVersion,
                                                                              ct);
            if (version == null)
                throw new InvalidOperationException(PublicationPointerTargetMissingDetail);
            publishedAtUtc = version.ScrapedAt;
        }

        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        bool updated = await sources.TryUpdateDirectoryPublicationAsync(publicationLease,
                                                                         definition.LastPublishedVersion,
                                                                         publishedAtUtc,
                                                                         publishedVersion,
                                                                         ct);
        if (!updated)
            throw new InvalidOperationException(DirectoryLifecycleLostDetail);
    }

    public async Task<LibraryDeletionResult> DeleteScanCandidateUnderLeaseAsync(
        string? profile,
        string libraryId,
        string version,
        IDirectoryPublicationLease publicationLease,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(publicationLease);
        ArgumentNullException.ThrowIfNull(modeLease);
        if (!string.Equals(publicationLease.LibraryId, libraryId, StringComparison.Ordinal))
            throw new ArgumentException("The directory lease belongs to a different library.",
                                        nameof(publicationLease));
        ValidateModeLease(modeLease, libraryId);
        if (modeLease.Mode != LibraryIngestionMode.Directory)
            throw new ArgumentException("Directory candidate cleanup requires a Directory-mode lease.",
                                        nameof(modeLease));

        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            publicationLease.OwnershipLostToken,
            modeLease.OwnershipLostToken);
        return await DeleteVersionCoreAsync(profile,
                                            libraryId,
                                            version,
                                            publicationLease,
                                            modeLease,
                                            preservedJobId: null,
                                            operation.Token);
    }

    private async Task<LibraryDeletionResult> DeleteVersionCoreAsync(
        string? profile,
        string libraryId,
        string version,
        IDirectoryPublicationLease? publicationLease,
        ILibraryIngestionModeLease? modeLease,
        string? preservedJobId,
        CancellationToken ct)
    {
        await RequireActiveModeLeaseAsync(modeLease, ct);
        await RequireActiveLeaseAsync(publicationLease, ct);

        var repositories = GetRepositories(profile);
        using var attempts = new DeletionAttempts(ct,
                                                   publicationLease?.OwnershipLostToken ?? default,
                                                   modeLease?.OwnershipLostToken ?? default);
        LibraryRecord? library = await attempts.AttemptAsync(
            token => repositories.Libraries.GetLibraryAsync(libraryId, token),
            fallback: null);
        LibraryVersionRecord? targetVersion = await attempts.AttemptAsync(
            token => repositories.Libraries.GetVersionAsync(libraryId, version, token),
            fallback: null);
        IReadOnlyList<LibraryVersionRecord> knownVersions = await attempts.AttemptAsync(
            token => repositories.Libraries.GetVersionsAsync(libraryId, token),
            Array.Empty<LibraryVersionRecord>());
        attempts.ThrowIfFailed();
        bool solePublishedPointer = library is { AllVersions.Count: 1 } &&
                                    string.Equals(library.AllVersions[index: 0],
                                                  version,
                                                  StringComparison.Ordinal);
        bool otherVersionExists = knownVersions.Any(candidate =>
                                                         !string.Equals(candidate.Version,
                                                                        version,
                                                                        StringComparison.Ordinal));
        bool deletesLibrary = solePublishedPointer && !otherVersionExists;
        LibraryDeletionResult result;
        if (deletesLibrary)
        {
            result = await DeleteLibraryCoreAsync(profile,
                                                  libraryId,
                                                  publicationLease,
                                                  modeLease,
                                                  deleteDefinition: false,
                                                  preservedJobId,
                                                  ct);
        }
        else
        {
            await RequireActiveModeLeaseAsync(modeLease, ct);
            await RequireActiveLeaseAsync(publicationLease, ct);
            var counts = await DeleteVersionStoresAsync(repositories,
                                                         libraryId,
                                                         version,
                                                         targetVersion,
                                                         publicationLease,
                                                         modeLease,
                                                         preservedJobId,
                                                         attempts);
            await attempts.AttemptAsync(token => mVectorSearch.RemoveIndexAsync(profile,
                                                                                  libraryId,
                                                                                  version,
                                                                                  token));
            if (!string.IsNullOrEmpty(targetVersion?.SubjectTaxonomyVersion))
            {
                bool catalogDeleted = await attempts.AttemptAsync(
                                          token => repositories.SubjectCatalogs.DeleteIfUnreferencedAsync(
                                              libraryId,
                                              targetVersion.SubjectTaxonomyVersion,
                                              version,
                                              token),
                                          fallback: false);
                if (catalogDeleted)
                    counts = counts with { SubjectCatalogs = counts.SubjectCatalogs + 1 };
            }
            await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
            await RequireActiveLeaseAsync(publicationLease, CancellationToken.None);
            attempts.ThrowIfFailed();

            await RequireActiveModeLeaseAsync(modeLease, ct);
            await RequireActiveLeaseAsync(publicationLease, ct);
            CancellationToken metadataToken = attempts.CurrentToken;
            metadataToken.ThrowIfCancellationRequested();
            var metadata = await repositories.Libraries.DeleteVersionAsync(libraryId,
                                                                             version,
                                                                             metadataToken);
            result = CreateResult(metadata, counts);
        }

        return result;
    }

    public Task<LibraryDeletionResult> DeleteLibraryAsync(string? profile,
                                                          string libraryId,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        return DeleteLibraryPublicAsync(profile, libraryId, preservedJobId: null, ct);
    }

    public async Task<LibraryDeletionResult> DeleteLibraryPreservingJobAsync(
        string? profile,
        string libraryId,
        string preservedJobId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(preservedJobId);
        return await DeleteLibraryPublicAsync(profile, libraryId, preservedJobId, ct);
    }

    private async Task<LibraryDeletionResult> DeleteLibraryPublicAsync(string? profile,
                                                                        string libraryId,
                                                                        string? preservedJobId,
                                                                        CancellationToken ct)
    {
        ILibraryIngestionModeLease acquired = await AcquireModeLeaseAsync(profile, libraryId, ct);
        await using ILibraryIngestionModeLease modeLease = acquired;
        LibraryDeletionResult result = await DeleteLibraryUnderModeLeaseInternalAsync(profile,
                                                                                        libraryId,
                                                                                        modeLease,
                                                                                        preservedJobId,
                                                                                        ct);
        bool ownershipDeleted = await modeLease.TryDeleteOwnershipAsync(CancellationToken.None);
        if (!ownershipDeleted)
            throw new InvalidOperationException(ModeLifecycleLostDetail);
        return result;
    }

    public Task<LibraryDeletionResult> DeleteLibraryUnderModeLeaseAsync(
        string? profile,
        string libraryId,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(modeLease);
        return DeleteLibraryUnderModeLeaseInternalAsync(profile,
                                                        libraryId,
                                                        modeLease,
                                                        preservedJobId: null,
                                                        ct);
    }

    private async Task<LibraryDeletionResult> DeleteLibraryUnderModeLeaseInternalAsync(
        string? profile,
        string libraryId,
        ILibraryIngestionModeLease modeLease,
        string? preservedJobId,
        CancellationToken ct)
    {
        ValidateModeLease(modeLease, libraryId);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            modeLease.OwnershipLostToken);
        await RequireActiveModeLeaseAsync(modeLease, operation.Token);
        return await DeleteLibraryWithPublicationLeaseAsync(profile,
                                                            libraryId,
                                                            modeLease,
                                                            preservedJobId,
                                                            operation.Token);
    }

    private async Task<LibraryDeletionResult> DeleteLibraryWithPublicationLeaseAsync(
        string? profile,
        string libraryId,
        ILibraryIngestionModeLease? modeLease,
        string? preservedJobId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryId, ct);
        LibraryDeletionResult result;
        if (definition == null)
        {
            result = await DeleteLibraryCoreAsync(profile,
                                                  libraryId,
                                                  publicationLease: null,
                                                  modeLease,
                                                  deleteDefinition: false,
                                                  preservedJobId,
                                                  ct);
        }
        else
        {
            IDirectoryPublicationLease? publicationLease =
                await sources.TryAcquireDirectoryPublicationLeaseAsync(
                    libraryId,
                    definition.RegistrationRevision,
                    definition.RegistrationIncarnationId,
                    $"delete-library-{Guid.NewGuid():N}",
                    definition.LastPublishedVersion,
                    ct);
            if (publicationLease == null)
                throw new InvalidOperationException(DirectoryLifecycleBusyDetail);

            await using(publicationLease)
            {
                result = await DeleteLibraryCoreAsync(profile,
                                                      libraryId,
                                                      publicationLease,
                                                      modeLease,
                                                      deleteDefinition: true,
                                                      preservedJobId,
                                                      ct);
            }
        }

        return result;
    }

    private async Task<LibraryDeletionResult> DeleteLibraryCoreAsync(
        string? profile,
        string libraryId,
        IDirectoryPublicationLease? publicationLease,
        ILibraryIngestionModeLease? modeLease,
        bool deleteDefinition,
        string? preservedJobId,
        CancellationToken ct)
    {
        await RequireActiveModeLeaseAsync(modeLease, ct);
        await RequireActiveLeaseAsync(publicationLease, ct);
        var repositories = GetRepositories(profile);
        using var attempts = new DeletionAttempts(ct,
                                                   publicationLease?.OwnershipLostToken ?? default,
                                                   modeLease?.OwnershipLostToken ?? default);
        var library = await attempts.AttemptAsync(token => repositories.Libraries.GetLibraryAsync(libraryId,
                                                                                                   token),
                                                  fallback: null);
        var versions = await attempts.AttemptAsync(token => repositories.Libraries.GetVersionsAsync(libraryId,
                                                                                                      token),
                                                   Array.Empty<LibraryVersionRecord>());
        IReadOnlySet<string> ownedVersions = await GetOwnedVersionsAsync(repositories,
                                                                         libraryId,
                                                                         library,
                                                                         versions,
                                                                         attempts);
        attempts.ThrowIfFailed();
        var counts = StoreDeletionCounts.Empty;
        foreach(string version in ownedVersions)
        {
            LibraryVersionRecord? versionRecord = versions.FirstOrDefault(candidate =>
                string.Equals(candidate.Version, version, StringComparison.Ordinal));
            var versionCounts = await DeleteVersionStoresAsync(repositories,
                                                                libraryId,
                                                                version,
                                                                versionRecord,
                                                                publicationLease,
                                                                modeLease,
                                                                preservedJobId,
                                                                attempts);
            counts = counts.Add(versionCounts);
        }

        await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
        await RequireActiveLeaseAsync(publicationLease, CancellationToken.None);
        var remainingDocumentRevisions = await attempts.AttemptAsync(
            token => repositories.SourceDocuments.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        var remainingSubjectAssignments = await attempts.AttemptAsync(
            token => repositories.SubjectAssignments.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        var remainingSubjectCatalogs = await attempts.AttemptAsync(
            token => repositories.SubjectCatalogs.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        var remainingDiffs = await attempts.AttemptAsync(
            token => repositories.Diffs.DeleteLibraryAsync(libraryId, token),
            fallback: 0L);
        bool removeLibraryReferences = deleteDefinition || publicationLease == null;
        var remainingJobs = removeLibraryReferences
                                ? await attempts.AttemptAsync(
                                      token => DeleteJobsAsync(repositories.Jobs,
                                                               libraryId,
                                                               version: null,
                                                               preservedJobId,
                                                               token),
                                      fallback: 0L)
                                : 0L;
        var projectProfiles = removeLibraryReferences
                                  ? await attempts.AttemptAsync(
                                        token => repositories.ProjectProfiles.RemoveIngestedPackageAsync(
                                            libraryId,
                                            token),
                                        fallback: 0L)
                                  : 0L;
        counts = counts with
                     {
                         DocumentRevisions = counts.DocumentRevisions + remainingDocumentRevisions,
                         SubjectAssignments = counts.SubjectAssignments + remainingSubjectAssignments,
                         SubjectCatalogs = counts.SubjectCatalogs + remainingSubjectCatalogs,
                         Diffs = counts.Diffs + remainingDiffs,
                         Jobs = counts.Jobs + remainingJobs
                     };
        await attempts.AttemptAsync(token => mVectorSearch.RemoveLibraryIndexesAsync(profile,
                                                                                      libraryId,
                                                                                      token));
        await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
        await RequireActiveLeaseAsync(publicationLease, CancellationToken.None);
        attempts.ThrowIfFailed();

        await RequireActiveModeLeaseAsync(modeLease, ct);
        await RequireActiveLeaseAsync(publicationLease, ct);
        CancellationToken metadataToken = attempts.CurrentToken;
        metadataToken.ThrowIfCancellationRequested();
        var versionsDeleted = await repositories.Libraries.DeleteAsync(libraryId, metadataToken);
        if (deleteDefinition && publicationLease != null)
        {
            await RequireActiveModeLeaseAsync(modeLease, metadataToken);
            await RequireActiveLeaseAsync(publicationLease, metadataToken);
            bool definitionDeleted = await repositories.SourceDocuments
                                                       .TryDeleteLeasedDirectoryDefinitionAsync(publicationLease,
                                                           metadataToken);
            if (!definitionDeleted)
                throw new InvalidOperationException(DirectoryLifecycleLostDetail);
        }

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
                                         SubjectCatalogs: counts.SubjectCatalogs,
                                         Diffs: counts.Diffs,
                                         Jobs: counts.Jobs,
                                         ProjectProfiles: projectProfiles);
    }

    private async Task<StoreDeletionCounts> DeleteVersionStoresAsync(DeletionRepositories repositories,
                                                                      string libraryId,
                                                                      string version,
        LibraryVersionRecord? targetVersion,
        IDirectoryPublicationLease? publicationLease,
        ILibraryIngestionModeLease? modeLease,
        string? preservedJobId,
        DeletionAttempts attempts)
    {
        await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
        await RequireActiveLeaseAsync(publicationLease, CancellationToken.None);
        IReadOnlyList<DocumentRevisionRecord> revisions = await attempts.AttemptAsync(
            token => repositories.SourceDocuments.GetRevisionsAsync(libraryId, version, token),
            Array.Empty<DocumentRevisionRecord>());
        IReadOnlyList<string> scanRunIds = revisions
                                           .Select(revision => revision.ScanRunId)
                                           .Append(targetVersion?.ScanRunId)
                                           .Where(scanRunId => !string.IsNullOrWhiteSpace(scanRunId))
                                           .OfType<string>()
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
        var jobs = await attempts.AttemptAsync(
            token => DeleteJobsAsync(repositories.Jobs,
                                     libraryId,
                                     version,
                                     preservedJobId,
                                     token),
            fallback: 0L);
        var diffs = await attempts.AttemptAsync(
            token => repositories.Diffs.DeleteVersionAsync(libraryId, version, token),
            fallback: 0L);
        long subjectAssignments = await attempts.AttemptAsync(
                                      token => repositories.SubjectAssignments.DeleteVersionAsync(libraryId,
                                                                                                     version,
                                                                                                     token),
                                      fallback: 0L);
        var subjectCatalogs = 0L;
        foreach(string scanRunId in scanRunIds)
        {
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
                                       subjectCatalogs,
                                       diffs,
                                       jobs);
    }

    private static async Task<IReadOnlySet<string>> GetOwnedVersionsAsync(
        DeletionRepositories repositories,
        string libraryId,
        LibraryRecord? library,
        IReadOnlyList<LibraryVersionRecord> versions,
        DeletionAttempts attempts)
    {
        var ownedVersions = new HashSet<string>(versions.Select(version => version.Version),
                                                StringComparer.Ordinal);
        if (library != null)
            ownedVersions.UnionWith(library.AllVersions);

        IReadOnlyList<IReadOnlyList<LibraryVersionKey>> childPairs =
        [
            await attempts.AttemptAsync(token => repositories.Pages.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Chunks.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Profiles.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Indexes.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Bm25Shards.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.ExcludedSymbols.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Audit.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.Diffs.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>()),
            await attempts.AttemptAsync(token => repositories.SourceDocuments.GetDistinctLibraryVersionPairsAsync(token),
                                        Array.Empty<LibraryVersionKey>())
        ];
        foreach(LibraryVersionKey pair in childPairs.SelectMany(pairs => pairs))
        {
            if (string.Equals(pair.LibraryId, libraryId, StringComparison.Ordinal))
                ownedVersions.Add(pair.Version);
        }

        return ownedVersions;
    }

    private static Task<long> DeleteJobsAsync(IJobRepository jobs,
                                               string libraryId,
                                               string? version,
                                               string? preservedJobId,
                                               CancellationToken ct) =>
        preservedJobId == null
            ? jobs.DeleteManyAsync(jobType: null,
                                   status: null,
                                   libraryId: libraryId,
                                   version: version,
                                   completedBefore: null,
                                   ct: ct)
            : jobs.DeleteManyExceptAsync(jobType: null,
                                         status: null,
                                         libraryId: libraryId,
                                         version: version,
                                         completedBefore: null,
                                         excludedJobId: preservedJobId,
                                         ct: ct);

    private DeletionRepositories GetRepositories(string? profile) =>
        new(mRepositoryFactory.GetLibraryRepository(profile),
            mRepositoryFactory.GetChunkRepository(profile),
            mRepositoryFactory.GetPageRepository(profile),
            mRepositoryFactory.GetLibraryProfileRepository(profile),
            mRepositoryFactory.GetLibraryIndexRepository(profile),
            mRepositoryFactory.GetBm25ShardRepository(profile),
            mRepositoryFactory.GetExcludedSymbolsRepository(profile),
            mRepositoryFactory.GetScrapeAuditRepository(profile),
            mRepositoryFactory.GetDiffRepository(profile),
            mRepositoryFactory.GetSourceDocumentRepository(profile),
            mRepositoryFactory.GetSubjectCatalogRepository(profile),
            mRepositoryFactory.GetSubjectAssignmentRepository(profile),
            mRepositoryFactory.GetJobRepository(profile),
            mRepositoryFactory.GetProjectProfileRepository(profile));

    private async Task<ILibraryIngestionModeLease> AcquireModeLeaseAsync(string? profile,
                                                                         string libraryId,
                                                                         CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ILibraryIngestionModeRepository modes =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        LibraryIngestionModeRecord? existing = await modes.GetAsync(libraryId, ct);
        if (!string.IsNullOrEmpty(existing?.PendingRenameOperationId))
            throw new InvalidOperationException(ModeLifecycleBusyDetail);

        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryId, ct);
        LibraryIngestionDataEvidence evidence = await modes.GetLibraryDataEvidenceAsync(libraryId, ct);
        bool hasDirectoryDefinition = definition != null || evidence.HasDirectoryDefinition;
        bool hasReservedWebLibrary = existing is
                                     {
                                         Mode: LibraryIngestionMode.Web,
                                         OwnershipState: LibraryIngestionOwnershipState.Reserved
                                     } && evidence.HasLibraryRecord;
        LibraryIngestionMode? detectedMode = hasDirectoryDefinition
                                                 ? LibraryIngestionMode.Directory
                                                 : (existing == null && evidence.HasLibraryRecord) ||
                                                   hasReservedWebLibrary
                                                     ? LibraryIngestionMode.Web
                                                     : null;
        if (existing is { OwnershipState: LibraryIngestionOwnershipState.Committed } &&
            hasDirectoryDefinition &&
            existing.Mode != LibraryIngestionMode.Directory)
        {
            throw new InvalidOperationException(ModeEvidenceConflictDetail);
        }

        LibraryIngestionMode requestedMode = existing?.Mode ?? detectedMode ?? LibraryIngestionMode.Web;
        ILibraryIngestionModeLease? lease = await mModeLeaseManager.TryAcquireAsync(profile,
                                                                                     libraryId,
                                                                                     requestedMode,
                                                                                     ct);
        if (lease == null)
            throw new InvalidOperationException(ModeLifecycleBusyDetail);

        try
        {
            if (lease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved &&
                detectedMode.HasValue)
            {
                bool committed = requestedMode == detectedMode.Value
                                     ? await lease.TryCommitAsync(ct)
                                     : await lease.TryReconcileReservedModeAsync(detectedMode.Value, ct);
                if (!committed)
                    throw new InvalidOperationException(ModeLifecycleLostDetail);
            }
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }

        return lease;
    }

    private async Task<bool> HasOwnedContentAsync(string? profile, string libraryId)
    {
        ILibraryIngestionModeRepository modes =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(
                                                    libraryId,
                                                    CancellationToken.None);
        LibraryIngestionDataEvidence evidence = await modes.GetLibraryDataEvidenceAsync(
                                                   libraryId,
                                                   CancellationToken.None);
        return definition != null ||
               evidence.HasOwnedContent;
    }

    private static async Task RequireActiveLeaseAsync(IDirectoryPublicationLease? publicationLease,
                                                       CancellationToken ct)
    {
        if (publicationLease != null)
        {
            if (publicationLease.OwnershipLostToken.IsCancellationRequested)
                throw new InvalidOperationException(DirectoryLifecycleLostDetail);
            bool renewed = await publicationLease.TryRenewAsync(ct);
            if (!renewed)
                throw new InvalidOperationException(DirectoryLifecycleLostDetail);
        }
    }

    private static async Task RequireActiveModeLeaseAsync(ILibraryIngestionModeLease? modeLease,
                                                           CancellationToken ct)
    {
        if (modeLease != null)
        {
            if (modeLease.OwnershipLostToken.IsCancellationRequested)
                throw new InvalidOperationException(ModeLifecycleLostDetail);
            bool renewed = await modeLease.TryRenewAsync(ct);
            if (!renewed)
                throw new InvalidOperationException(ModeLifecycleLostDetail);
        }
    }

    private static void ValidateModeLease(ILibraryIngestionModeLease modeLease, string libraryId)
    {
        ArgumentNullException.ThrowIfNull(modeLease);
        if (!string.Equals(modeLease.LibraryId, libraryId, StringComparison.Ordinal))
            throw new ArgumentException("The ingestion-mode lease belongs to a different library.",
                                        nameof(modeLease));
    }

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
            counts.SubjectCatalogs,
            counts.Diffs,
            counts.Jobs);

    private sealed record DeletionRepositories(ILibraryRepository Libraries,
                                               IChunkRepository Chunks,
                                               IPageRepository Pages,
                                               ILibraryProfileRepository Profiles,
                                               ILibraryIndexRepository Indexes,
                                               IBm25ShardRepository Bm25Shards,
                                               IExcludedSymbolsRepository ExcludedSymbols,
                                               IScrapeAuditRepository Audit,
                                               IDiffRepository Diffs,
                                               ISourceDocumentRepository SourceDocuments,
                                               ISubjectCatalogRepository SubjectCatalogs,
                                               ISubjectAssignmentRepository SubjectAssignments,
                                               IJobRepository Jobs,
                                               IProjectProfileRepository ProjectProfiles);

    private const string DirectoryLifecycleBusyDetail =
        "The directory library is currently being scanned or deleted. Try again after that operation finishes.";
    private const string DirectoryLifecycleLostDetail =
        "The directory deletion no longer owns its lifecycle lease.";
    private const string ModeLifecycleLostDetail =
        "The library deletion no longer owns its ingestion-mode lease.";
    private const string ModeLifecycleBusyDetail =
        "The library is currently being ingested, deleted, or renamed. Try again after that operation finishes.";
    private const string ModeEvidenceConflictDetail =
        "The durable ingestion mode conflicts with the library's stored data. Repair is required before deletion.";
    private const string PublicationPointerTargetMissingDetail =
        "The remaining library pointer does not reference an available version row.";

    private sealed record StoreDeletionCounts(long Chunks,
                                               long Pages,
                                               long Profiles,
                                               long Indexes,
                                               long Bm25Shards,
                                               long ExcludedSymbols,
                                               long AuditEntries,
                                               long DocumentRevisions,
                                               long SubjectAssignments,
                                               long SubjectCatalogs,
                                               long Diffs,
                                               long Jobs)
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
                                           SubjectCatalogs + other.SubjectCatalogs,
                                           Diffs + other.Diffs,
                                           Jobs + other.Jobs);
        }

        internal static StoreDeletionCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class DeletionAttempts : IDisposable
    {
        internal DeletionAttempts(CancellationToken requestedToken,
                                  CancellationToken publicationOwnershipLostToken = default,
                                  CancellationToken modeOwnershipLostToken = default)
        {
            if (publicationOwnershipLostToken.CanBeCanceled && modeOwnershipLostToken.CanBeCanceled)
            {
                mOwnershipTokens = CancellationTokenSource.CreateLinkedTokenSource(
                    publicationOwnershipLostToken,
                    modeOwnershipLostToken);
                mOwnershipLostToken = mOwnershipTokens.Token;
            }
            else
            {
                mOwnershipLostToken = publicationOwnershipLostToken.CanBeCanceled
                                          ? publicationOwnershipLostToken
                                          : modeOwnershipLostToken;
            }

            if (mOwnershipLostToken.CanBeCanceled)
            {
                mRequestedAndOwnership = CancellationTokenSource.CreateLinkedTokenSource(requestedToken,
                                                                                           mOwnershipLostToken);
                mInitialToken = mRequestedAndOwnership.Token;
            }
            else
            {
                mInitialToken = requestedToken;
            }
        }

        private ExceptionDispatchInfo? mFailure;
        private readonly CancellationToken mInitialToken;
        private readonly CancellationTokenSource? mOwnershipTokens;
        private readonly CancellationTokenSource? mRequestedAndOwnership;
        private readonly CancellationToken mOwnershipLostToken;

        internal CancellationToken CurrentToken => mFailure == null
                                                        ? mInitialToken
                                                        : mOwnershipLostToken;

        internal async Task<T> AttemptAsync<T>(Func<CancellationToken, Task<T>> operation, T fallback)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var result = fallback;
            try
            {
                CancellationToken token = CurrentToken;
                token.ThrowIfCancellationRequested();
                result = await operation(token);
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
                CancellationToken token = CurrentToken;
                token.ThrowIfCancellationRequested();
                await operation(token);
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

        public void Dispose()
        {
            mRequestedAndOwnership?.Dispose();
            mOwnershipTokens?.Dispose();
        }
    }
}
