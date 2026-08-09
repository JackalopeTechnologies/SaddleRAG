// LibraryImporter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Reads a .srlib.zip bundle and writes it into the receiver's
///     MongoDB. Tasks 1–15: manifest read, sha256 validation,
///     pathological-id guard. Task 16: conflict check, concurrent-job
///     guard, encoder-match decision. Task 17: per-version write with
///     rollback (encoder-match path). Task 18 adds BM25 GridFS re-upload,
///     Task 19 encoder-mismatch reembed enqueue, Task 20 overwrite path.
/// </summary>
public sealed class LibraryImporter
{
    #region Dependency fields

    private readonly ILibraryRepository mLibraryRepository;
    private readonly IJobRepository mJobRepository;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILibraryProfileRepository mProfileRepository;
    private readonly ILibraryIndexRepository mIndexRepository;
    private readonly IExcludedSymbolsRepository mExcludedSymbolsRepository;
    private readonly IDiffRepository mDiffRepository;
    private readonly IPageRepository mPageRepository;
    private readonly IChunkRepository mChunkRepository;
    private readonly IBm25ShardRepository mBm25Repository;
    private readonly ISourceDocumentRepository? mSourceDocumentRepository;
    private readonly ISubjectCatalogRepository? mSubjectCatalogRepository;
    private readonly ISubjectAssignmentRepository? mSubjectAssignmentRepository;
    private readonly ICollectionCompactor? mCompactor;
    private readonly Func<string?, IMongoDatabase>? mDatabaseResolver;
    private readonly ILibraryDeletionService? mDeletionService;
    private readonly ILibraryIngestionModeLeaseManager? mModeLeaseManager;
    private readonly ILibraryIngestionModeRepository? mModeRepository;
    private readonly IReembedJobDispatcher? mReembedJobDispatcher;

    #endregion

    #region Constants

    private const string OverwriteHint = "Pass overwrite=true to replace.";
    private const string ConcurrentJobHint = "Wait for it to complete or cancel it before retrying.";
    private const int PageBatchSize = 256;
    private const int ChunkBatchSize = 256;
    private const string ReembedItemsLabel = "chunks";
    private const string FollowUpSeparator = "; ";
    private const int BytesPerFloat = 4;
    private const int EstimatedBytesPerPage = 50_000;
    private const double Bm25AverageTolerance = 1e-12;
    private const uint Bm25HashSeed = 2166136261u;
    private const uint Bm25HashMultiplier = 16777619u;
    private const string LibraryVersionRecordType = "library-version";
    private const string LibraryProfileRecordType = "library-profile";
    private const string LibraryIndexRecordType = "library-index";
    private const string DocumentRevisionRecordType = "document-revision";
    private const string SubjectAssignmentRecordType = "subject-assignment";
    private const string ExcludedSymbolRecordType = "excluded-symbol";
    private const string PageRecordType = "page";
    private const string ChunkRecordType = "chunk";
    private const string Bm25ShardRecordType = "BM25 shard";
    private const string SourceDocumentIdPrefix = "source-document-";
    private const string DocumentPageIdPrefix = "document-page-";
    private const string PageSectionMarker = "#section-";
    private const string PrimarySubjectRole = "primary";
    private const string SecondarySubjectRole = "secondary";
    private const char IdentitySeparator = '\u001f';
    #endregion

    public LibraryImporter(ILibraryRepository libraryRepository,
                           IJobRepository jobRepository,
                           IEmbeddingProvider embeddingProvider,
                           ILibraryProfileRepository profileRepository,
                           ILibraryIndexRepository indexRepository,
                           IExcludedSymbolsRepository excludedSymbolsRepository,
                           IDiffRepository diffRepository,
                           IPageRepository pageRepository,
                           IChunkRepository chunkRepository,
                           IBm25ShardRepository bm25Repository,
                           ICollectionCompactor? compactor = null,
                           Func<string?, IMongoDatabase>? databaseResolver = null,
                           ILibraryDeletionService? deletionService = null,
                           ILibraryIngestionModeLeaseManager? modeLeaseManager = null,
                           ILibraryIngestionModeRepository? modeRepository = null,
                           IReembedJobDispatcher? reembedJobDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(profileRepository);
        ArgumentNullException.ThrowIfNull(indexRepository);
        ArgumentNullException.ThrowIfNull(excludedSymbolsRepository);
        ArgumentNullException.ThrowIfNull(diffRepository);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(bm25Repository);
        mLibraryRepository = libraryRepository;
        mJobRepository = jobRepository;
        mEmbeddingProvider = embeddingProvider;
        mProfileRepository = profileRepository;
        mIndexRepository = indexRepository;
        mExcludedSymbolsRepository = excludedSymbolsRepository;
        mDiffRepository = diffRepository;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mBm25Repository = bm25Repository;
        mCompactor = compactor;
        mDatabaseResolver = databaseResolver;
        mDeletionService = deletionService;
        mModeLeaseManager = modeLeaseManager;
        mModeRepository = modeRepository;
        mReembedJobDispatcher = reembedJobDispatcher;
    }

    public LibraryImporter(ILibraryRepository libraryRepository,
                           IJobRepository jobRepository,
                           IEmbeddingProvider embeddingProvider,
                           ILibraryProfileRepository profileRepository,
                           ILibraryIndexRepository indexRepository,
                           IExcludedSymbolsRepository excludedSymbolsRepository,
                           IDiffRepository diffRepository,
                           IPageRepository pageRepository,
                           IChunkRepository chunkRepository,
                           IBm25ShardRepository bm25Repository,
                           ISourceDocumentRepository sourceDocumentRepository,
                           ISubjectCatalogRepository subjectCatalogRepository,
                           ISubjectAssignmentRepository subjectAssignmentRepository,
                           ICollectionCompactor? compactor = null,
                           Func<string?, IMongoDatabase>? databaseResolver = null,
                           ILibraryDeletionService? deletionService = null,
                           ILibraryIngestionModeLeaseManager? modeLeaseManager = null,
                           ILibraryIngestionModeRepository? modeRepository = null,
                           IReembedJobDispatcher? reembedJobDispatcher = null)
        : this(libraryRepository,
               jobRepository,
               embeddingProvider,
               profileRepository,
               indexRepository,
               excludedSymbolsRepository,
               diffRepository,
               pageRepository,
               chunkRepository,
               bm25Repository,
               compactor,
               databaseResolver,
               deletionService,
               modeLeaseManager,
               modeRepository,
               reembedJobDispatcher)
    {
        ArgumentNullException.ThrowIfNull(sourceDocumentRepository);
        ArgumentNullException.ThrowIfNull(subjectCatalogRepository);
        ArgumentNullException.ThrowIfNull(subjectAssignmentRepository);
        mSourceDocumentRepository = sourceDocumentRepository;
        mSubjectCatalogRepository = subjectCatalogRepository;
        mSubjectAssignmentRepository = subjectAssignmentRepository;
    }

    #region Active encoder properties

    // ProviderId is surfaced directly from IEmbeddingProvider.
    private string ActiveEncoderProviderId => mEmbeddingProvider.ProviderId;

    private string ActiveEncoderModelName => mEmbeddingProvider.ModelName;

    private int ActiveEncoderDimensions => mEmbeddingProvider.Dimensions;

    private bool EncoderMatches(BundleVersionEntry versionEntry) =>
        string.Equals(versionEntry.EmbeddingProviderId, ActiveEncoderProviderId, StringComparison.Ordinal)
        && string.Equals(versionEntry.EmbeddingModelName, ActiveEncoderModelName, StringComparison.Ordinal)
        && versionEntry.EmbeddingDimensions == ActiveEncoderDimensions;

    private void ValidateReembedDispatcherAvailability(BundleManifest manifest)
    {
        if (mReembedJobDispatcher == null && manifest.Versions.Any(version => !EncoderMatches(version)))
        {
            throw new InvalidOperationException(
                "This package requires re-embedding, but no re-embed job dispatcher is configured.");
        }
    }

    #endregion

    public async Task<ImportResult> ImportAsync(ImportRequest request,
                                                IProgress<ImportProgress>? progress,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.BundlePath);
        if (!File.Exists(request.BundlePath))
            throw new FileNotFoundException("Bundle not found", request.BundlePath);

        using var reader = new ZipBundleReader(request.BundlePath);

        var manifest = await ReadManifestAsync(reader, ct);
        if (manifest.ManifestVersion > BundlePaths.CurrentManifestVersion)
            throw new InvalidOperationException(
                $"Bundle was produced by a newer SaddleRAG (manifestVersion={manifest.ManifestVersion}); upgrade to import.");

        ValidateManifestIdentities(manifest);
        ValidateDirectoryOptions(manifest.Directory);
        if (manifest.Directory != null && manifest.Versions.Count == 0)
            throw new InvalidDataException("A directory-library bundle must contain at least one version.");
        if (manifest.Directory != null && mDeletionService == null)
            throw new InvalidOperationException(
                "Directory-library import requires the shared lifecycle deletion service.");

        ValidateAllBlobs(reader, manifest, ct);

        ValidatedImportPackage package = await MaterializeAndValidatePackageAsync(reader, manifest, ct);
        ValidateDocumentRepositoryAvailability(package);
        ValidateReembedDispatcherAvailability(manifest);
        await ValidateExistingSubjectCatalogsAsync(package.Catalogs, ct);

        ImportModeScope modeScope = await AcquireImportModeAsync(manifest,
                                                                  request.Profile,
                                                                  ct);
        ImportResult result;
        Exception? importFailure = null;
        try
        {
            ct = modeScope.Token;
            result = await ImportWithCleanupAsync(reader,
                                                  manifest,
                                                  request,
                                                  modeScope,
                                                  package,
                                                  progress,
                                                  ct);
        }
        catch(Exception ex)
        {
            importFailure = ex;
            throw;
        }
        finally
        {
            await DisposeImportModeScopeAsync(modeScope, importFailure);
        }
        return result;
    }

    private async Task<ImportResult> ImportWithCleanupAsync(
        IBundleReader reader,
        BundleManifest manifest,
        ImportRequest request,
        ImportModeScope modeScope,
        ValidatedImportPackage package,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        ImportResult result;
        try
        {
            result = await ImportUnderHeldScopeAsync(reader,
                                                     manifest,
                                                     request,
                                                     modeScope,
                                                     package,
                                                     progress,
                                                     ct);
        }
        catch(Exception ex) when (modeScope is
                                  {
                                      OwnsNewLibraryData: true,
                                      OwnershipCommitted: true,
                                      PublicationEstablished: false,
                                      PublicationOutcomeUnknown: false,
                                      CleanupAttempted: false
                                  })
        {
            await DeleteNewLibraryAfterFailureAsync(manifest.Library.Id,
                                                     request.Profile,
                                                     modeScope,
                                                     modeScope.VersionWriteLogs,
                                                     ex);
            throw;
        }

        return result;
    }

    private async Task<ImportResult> ImportUnderHeldScopeAsync(
        IBundleReader reader,
        BundleManifest manifest,
        ImportRequest request,
        ImportModeScope modeScope,
        ValidatedImportPackage package,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        var versionsImported = new List<string>();
        var versionsRequiringReembed = new List<string>();
        var partialFailures = new List<ImportPartialFailure>();
        var overwroteVersions = new List<string>();
        var attemptedPurgeVersions = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, VersionWriteLog> versionWriteLogs = modeScope.VersionWriteLogs;
        string importOperationId = $"package-import-{Guid.NewGuid():N}";
        long bytesFreed = 0;
        LibraryRecord? existingLibrary = null;
        bool ownsNewLibraryData = modeScope.OwnsNewLibraryData;

        bool hasVersions = manifest.Versions.Count > 0;
        if (hasVersions)
        {
            // Gate 1 — conflict scan.
            existingLibrary = await mLibraryRepository.GetLibraryAsync(manifest.Library.Id, ct);
            IReadOnlyList<LibraryVersionRecord> existingVersionRows =
                await mLibraryRepository.GetVersionsAsync(manifest.Library.Id, ct) ?? [];
            var existingVersions = new HashSet<string>(StringComparer.Ordinal);
            if (existingLibrary != null)
                existingVersions.UnionWith(existingLibrary.AllVersions);
            existingVersions.UnionWith(existingVersionRows.Select(version => version.Version));

            var conflicting = manifest.Versions
                                      .Select(v => v.Version)
                                      .Where(existingVersions.Contains)
                                      .Distinct(StringComparer.Ordinal)
                                      .ToList();
            if (conflicting.Count > 0 && !request.Overwrite)
                throw new InvalidOperationException(
                    $"Versions already present on receiver: {string.Join(", ", conflicting)}. {OverwriteHint}");

            // Gate 2 — concurrent-job guard.
            foreach (var manifestVersion in manifest.Versions)
            {
                var running = await mJobRepository.ListActiveAsync(manifest.Library.Id,
                                                                   manifestVersion.Version,
                                                                   ct: ct);
                if (running.Count > 0)
                {
                    var first = running[0];
                    throw new InvalidOperationException(
                        $"Cannot import: job {first.Id} (type={first.JobType}, status={first.Status}) is already " +
                        $"running for {manifest.Library.Id}/{manifestVersion.Version}. {ConcurrentJobHint}");
                }
            }

            // Recheck immutable catalogs while holding the ingestion-mode lease. The
            // pre-acquisition check keeps invalid imports cheap, while this check
            // closes the race with a catalog inserted before destructive writes.
            await ValidateExistingSubjectCatalogsAsync(package.Catalogs, ct);
            await PrepareImportForWritesAsync(modeScope, ct);

            // Overwrite: purge pre-existing versions before writing new data.
            // This ordering is intentional: purge runs before the per-version write loop
            // so that rollback on a subsequent write failure can safely use
            // DeleteAsync(libraryId, version) without risk of removing pre-existing rows
            // — there are none at that point.
            if (request.Overwrite && conflicting.Count > 0)
            {
                bytesFreed += await PurgeConflictingVersionsAsync(manifest,
                                                                   request,
                                                                   modeScope,
                                                                   existingLibrary,
                                                                   conflicting,
                                                                   attemptedPurgeVersions,
                                                                   overwroteVersions,
                                                                   ct);
            }

            // Per-version write loop with rollback.
            for (int i = 0; i < manifest.Versions.Count; i++)
            {
                var versionEntry = manifest.Versions[i];
                bool encoderMatches = EncoderMatches(versionEntry);
                progress?.Report(new ImportProgress
                                     {
                                         CurrentVersion = versionEntry.Version,
                                         CurrentStep = "writing",
                                         VersionIndex = i,
                                         TotalVersions = manifest.Versions.Count
                                     });

                var log = new VersionWriteLog();
                modeScope.TrackVersionWriteLog(versionEntry.Version, log);
                try
                {
                    await WriteVersionAsync(reader,
                                            versionEntry,
                                            package.Versions[versionEntry.Version],
                                            package.Sources,
                                            package.Catalogs,
                                            encoderMatches,
                                            importOperationId,
                                            log,
                                            ct);
                    versionsImported.Add(versionEntry.Version);
                    versionsRequiringReembed.AddRange(encoderMatches
                                                           ? []
                                                           : [versionEntry.Version]);
                }
                catch(ImportPublicationOutcomeUnknownException)
                {
                    modeScope.MarkPublicationOutcomeUnknown();
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    await RollbackAndReconcileFailedVersionAsync(manifest.Library.Id,
                                                                 versionEntry.Version,
                                                                 request.Profile,
                                                                 modeScope,
                                                                 manifest.Library,
                                                                 existingLibrary,
                                                                 overwroteVersions,
                                                                 log,
                                                                 ex);
                    throw;
                }
                catch (Exception ex)
                {
                    await RollbackAndReconcileFailedVersionAsync(manifest.Library.Id,
                                                                 versionEntry.Version,
                                                                 request.Profile,
                                                                 modeScope,
                                                                 manifest.Library,
                                                                 existingLibrary,
                                                                 overwroteVersions,
                                                                 log,
                                                                 ex);
                    partialFailures.Add(new ImportPartialFailure
                                            {
                                                Version = versionEntry.Version,
                                                Reason = ex.Message
                                            });
                    break;
                }
            }


            if (versionsImported.Count == 0 &&
                partialFailures.Count > 0 &&
                existingLibrary == null &&
                ownsNewLibraryData)
            {
                await DeleteNewLibraryAfterFailureAsync(manifest.Library.Id,
                                                        request.Profile,
                                                        modeScope,
                                                        versionWriteLogs,
                                                        new InvalidOperationException(
                                                            partialFailures[index: 0].Reason));
            }
        }

        LibraryRecord? importedLibrary = null;
        var pendingReembedJobIds = new List<string>();
        var attemptedReembedJobs = new List<AttemptedReembedJob>();
        var confirmedReembedJobs = new List<AttemptedReembedJob>();
        var versionsRequiringManualReembed = new List<string>();
        try
        {
            // Library record upsert — merge existing AllVersions with newly imported versions.
            if (versionsImported.Count > 0)
                importedLibrary = await PublishLibrarySummaryAsync(package.Library,
                                                                   manifest,
                                                                   versionsImported,
                                                                   modeScope,
                                                                   ct);

            foreach(string version in versionsRequiringReembed)
            {
                AttemptedReembedJob attempted = CreateReembedJob(manifest.Library.Id,
                                                                  version,
                                                                  request.Profile);
                attemptedReembedJobs.Add(attempted);
                try
                {
                    await PersistReembedJobAsync(attempted, modeScope, ct);
                    confirmedReembedJobs.Add(attempted);
                    pendingReembedJobIds.Add(attempted.Record.Id);
                }
                catch(Exception ex) when (!modeScope.PublicationOutcomeUnknown &&
                                          overwroteVersions.Contains(version,
                                                                      StringComparer.Ordinal))
                {
                    versionsRequiringManualReembed.Add(version);
                    partialFailures.Add(new ImportPartialFailure
                                            {
                                                Version = version,
                                                Reason =
                                                    $"The replacement was preserved, but its re-embed job could not be queued: {ex.Message}"
                                            });
                }
            }

            if (request.Compact && overwroteVersions.Count > 0 && mCompactor != null && mDatabaseResolver != null)
            {
                var database = mDatabaseResolver(request.Profile);
                foreach (var name in mCompactor.DefaultHotCollections)
                    await mCompactor.CompactAsync(database, name, ct);
            }

            if (manifest.Directory == null && importedLibrary != null)
                modeScope.MarkPublicationEstablished();

            if (versionsImported.Count > 0 && manifest.Directory != null && mSourceDocumentRepository != null)
                await PublishDirectoryImportAsync(manifest,
                                                  modeScope,
                                                  mSourceDocumentRepository,
                                                  importedLibrary,
                                                  ct);
        }
        catch(Exception ex) when (versionsImported.Count > 0 &&
                                  !modeScope.PublicationEstablished &&
                                  !modeScope.PublicationOutcomeUnknown)
        {
            await CleanupFailedImportAsync(manifest.Library,
                                           request.Profile,
                                           modeScope,
                                           versionsImported,
                                           versionWriteLogs,
                                           overwroteVersions,
                                           existingLibrary,
                                           attemptedReembedJobs,
                                           ex);
            throw;
        }
        finally
        {
            foreach(AttemptedReembedJob confirmed in confirmedReembedJobs.Where(job =>
                        modeScope.PublicationEstablished ||
                        overwroteVersions.Contains(job.Version, StringComparer.Ordinal)))
            {
                mReembedJobDispatcher?.TryDispatchPersisted(confirmed.Record);
            }
        }

        var recommendedFollowUp = BuildRecommendedFollowUp(pendingReembedJobIds,
                                                             versionsRequiringManualReembed,
                                                             bytesFreed: bytesFreed,
                                                             overwroteAny: overwroteVersions.Count > 0);

        return new ImportResult
                   {
                       LibraryId = manifest.Library.Id,
                       VersionsImported = versionsImported,
                       OverwrittenVersions = overwroteVersions,
                       BytesFreed = bytesFreed,
                       PendingReembedJobIds = pendingReembedJobIds,
                       PartialFailures = partialFailures,
                       RecommendedFollowUp = recommendedFollowUp
                   };
    }

    private async Task<long> PurgeConflictingVersionsAsync(
        BundleManifest manifest,
        ImportRequest request,
        ImportModeScope modeScope,
        LibraryRecord? existingLibrary,
        IReadOnlyCollection<string> conflicting,
        HashSet<string> attemptedPurgeVersions,
        ICollection<string> overwroteVersions,
        CancellationToken ct)
    {
        long bytesFreed = 0;
        try
        {
            foreach(string conflictingVersion in conflicting)
            {
                attemptedPurgeVersions.Add(conflictingVersion);
                bytesFreed += await PurgeVersionAsync(
                                  manifest.Library.Id,
                                  manifest.Versions.First(version => version.Version == conflictingVersion),
                                  request.Profile,
                                  modeScope,
                                  ct);
                overwroteVersions.Add(conflictingVersion);
            }
        }
        catch(Exception purgeFailure)
        {
            try
            {
                await ReconcilePublicationAfterDestructiveAttemptsAsync(manifest.Library.Id,
                                                                         manifest.Library,
                                                                         existingLibrary,
                                                                         attemptedPurgeVersions,
                                                                         modeScope);
            }
            catch(Exception recoveryFailure)
            {
                throw new AggregateException(
                    $"Overwrite purge failed for {manifest.Library.Id}, and publication recovery also failed.",
                    purgeFailure,
                    recoveryFailure);
            }

            throw;
        }

        return bytesFreed;
    }

    private async Task<ImportModeScope> AcquireImportModeAsync(BundleManifest manifest,
                                                               string? profile,
                                                               CancellationToken ct)
    {
        LibraryIngestionMode requestedMode = manifest.Directory == null
                                                 ? LibraryIngestionMode.Web
                                                 : LibraryIngestionMode.Directory;
        ILibraryIngestionModeLeaseManager manager = mModeLeaseManager ??
                                                    throw new InvalidOperationException(
                                                        "Package import requires the ingestion-mode lease manager.");
        ILibraryIngestionModeRepository modes = mModeRepository ??
                                                throw new InvalidOperationException(
                                                    "Package import requires the ingestion-mode repository.");
        ILibraryIngestionModeLease? lease = await manager.TryAcquireAsync(profile,
                                                                           manifest.Library.Id,
                                                                           requestedMode,
                                                                           ct);
        if (lease == null)
        {
            throw new InvalidOperationException(
                "The library is busy or is owned by a different ingestion mode; package import was not started.");
        }

        var scope = new ImportModeScope(lease, ct);
        ImportModeScope result;
        try
        {
            if (manifest.Directory == null)
                await ValidateWebImportOwnershipAsync(manifest, modes, lease, scope);
            else
                await ConfigureDirectoryImportAsync(manifest, modes, lease, scope);

            bool renewed = await lease.TryRenewAsync(scope.Token);
            if (!renewed)
                throw new InvalidOperationException("The package import lost its ingestion-mode lease.");
            result = scope;
        }
        catch(Exception acquisitionFailure)
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch(Exception disposalFailure)
            {
                throw new AggregateException(
                    "Package import mode acquisition failed, and acquired-scope cleanup also failed.",
                    acquisitionFailure,
                    disposalFailure);
            }

            throw;
        }

        return result;
    }

    private static async Task DisposeImportModeScopeAsync(ImportModeScope modeScope,
                                                          Exception? importFailure)
    {
        try
        {
            await modeScope.DisposeAsync();
        }
        catch(Exception disposalFailure) when (importFailure != null)
        {
            throw new AggregateException(
                "Package import failed, and acquired-scope cleanup also failed.",
                importFailure,
                disposalFailure);
        }
    }

    private async Task PublishDirectoryImportAsync(BundleManifest manifest,
                                                    ImportModeScope modeScope,
                                                    ISourceDocumentRepository sources,
                                                    LibraryRecord? importedLibrary,
                                                    CancellationToken ct)
    {
        await ApplyDirectoryPackagePublicationAsync(manifest,
                                                    modeScope,
                                                    sources,
                                                    importedLibrary,
                                                    ct);
        modeScope.MarkPublicationEstablished();
    }

    private async Task ApplyDirectoryPackagePublicationAsync(BundleManifest manifest,
                                                              ImportModeScope modeScope,
                                                              ISourceDocumentRepository sources,
                                                              LibraryRecord? importedLibrary,
                                                              CancellationToken ct)
    {
        LibraryRecord publishedLibrary = importedLibrary ??
                                           throw new InvalidOperationException(
                                               "The imported library metadata was not available for publication.");
        LibraryVersionRecord publishedVersion = await mLibraryRepository.GetVersionAsync(
                                                    manifest.Library.Id,
                                                    publishedLibrary.CurrentVersion,
                                                    ct) ?? throw new InvalidOperationException(
                                                    "The imported current version was not available for publication.");
        DirectoryLibraryDefinition packageDefinition = CreatePackageDefinition(manifest, publishedVersion);
        if (modeScope.PublicationLease == null)
        {
            await sources.UpsertDirectoryDefinitionAsync(packageDefinition, ct);
        }
        else
        {
            await RequireActivePublicationLeaseAsync(modeScope.PublicationLease, ct);
            bool applied;
            try
            {
                applied = await sources.TryApplyDirectoryPackagePublicationAsync(
                              modeScope.PublicationLease,
                              modeScope.ExistingDirectoryDefinition?.LastPublishedVersion,
                              packageDefinition,
                              publishedVersion.ScrapedAt,
                              publishedLibrary.CurrentVersion,
                              ct);
            }
            catch(Exception publicationFailure)
            {
                try
                {
                    DirectoryLibraryDefinition? current = await sources.GetDirectoryDefinitionAsync(
                                                                      manifest.Library.Id,
                                                                      CancellationToken.None);
                    applied = DirectoryPackagePublicationMatches(current,
                                                                 modeScope.PublicationLease,
                                                                 packageDefinition,
                                                                 publishedVersion.ScrapedAt,
                                                                 publishedLibrary.CurrentVersion);
                    ThrowIfPublicationOutcomeCannotBeAttributed(applied,
                                                                 current,
                                                                 modeScope.PublicationLease,
                                                                 publishedLibrary.CurrentVersion);
                }
                catch(Exception confirmationFailure)
                {
                    modeScope.MarkPublicationOutcomeUnknown();
                    throw new AggregateException(
                        "Directory package publication failed and its durable outcome could not be confirmed.",
                        publicationFailure,
                        confirmationFailure);
                }

                if (!applied)
                    throw;
            }
            if (!applied)
                throw new InvalidOperationException("The directory import lost its publication lease.");
        }
    }

    private static bool DirectoryPackagePublicationMatches(DirectoryLibraryDefinition? current,
                                                           IDirectoryPublicationLease lease,
                                                           DirectoryLibraryDefinition packageDefinition,
                                                           DateTime publishedAtUtc,
                                                           string publishedVersion) =>
        current != null &&
        DirectoryPublicationOwnerMatches(current, lease) &&
        string.Equals(current.Name, packageDefinition.Name, StringComparison.Ordinal) &&
        string.Equals(current.Hint, packageDefinition.Hint, StringComparison.Ordinal) &&
        current.Recursive == packageDefinition.Recursive &&
        current.AllowedExtensions.SequenceEqual(packageDefinition.AllowedExtensions, StringComparer.Ordinal) &&
        current.ExclusionPatterns.SequenceEqual(packageDefinition.ExclusionPatterns, StringComparer.Ordinal) &&
        current.LastPublishedAtUtc == publishedAtUtc &&
        string.Equals(current.LastPublishedVersion, publishedVersion, StringComparison.Ordinal);

    private static bool DirectoryPublicationOwnerMatches(DirectoryLibraryDefinition? current,
                                                         IDirectoryPublicationLease lease) =>
        current != null &&
        current.RegistrationRevision == lease.RegistrationRevision &&
        string.Equals(current.RegistrationIncarnationId,
                      lease.RegistrationIncarnationId,
                      StringComparison.Ordinal) &&
        string.Equals(current.PublicationLeaseScanRunId, lease.ScanRunId, StringComparison.Ordinal) &&
        current.PublicationLeaseRegistrationRevision == lease.RegistrationRevision;

    private static void ThrowIfPublicationOutcomeCannotBeAttributed(
        bool applied,
        DirectoryLibraryDefinition? current,
        IDirectoryPublicationLease lease,
        string publishedVersion)
    {
        bool ownerMatches = DirectoryPublicationOwnerMatches(current, lease);
        bool desiredPointerObserved = string.Equals(current?.LastPublishedVersion,
                                                    publishedVersion,
                                                    StringComparison.Ordinal);
        if (!applied && (!ownerMatches || desiredPointerObserved))
        {
            throw new InvalidOperationException(
                "The observed directory publication could not be attributed to the held lease.");
        }
    }

    private async Task CleanupFailedImportAsync(
        BundleLibraryInfo packageLibrary,
        string? profile,
        ImportModeScope modeScope,
        IEnumerable<string> versionsImported,
        IReadOnlyDictionary<string, VersionWriteLog> versionWriteLogs,
        IReadOnlyCollection<string> overwrittenVersions,
        LibraryRecord? previousLibrary,
        IReadOnlyCollection<AttemptedReembedJob> attemptedReembedJobs,
        Exception importFailure)
    {
        string libraryId = packageLibrary.Id;
        if (modeScope.OwnsNewLibraryData)
        {
            await DeleteNewLibraryAfterFailureAsync(libraryId,
                                                     profile,
                                                     modeScope,
                                                     versionWriteLogs,
                                                     importFailure);
        }
        else
        {
            var cleanupFailures = new List<Exception>();
            var preservedReplacements = overwrittenVersions.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<string> rollbackVersions = Enumerable.Reverse(versionsImported)
                                                               .Where(version =>
                                                                   !preservedReplacements.Contains(version))
                                                               .ToList();
            foreach(string importedVersion in rollbackVersions)
            {
                VersionWriteLog log = versionWriteLogs[importedVersion];
                try
                {
                    await DeleteImportedVersionAsync(libraryId,
                                                     importedVersion,
                                                     profile,
                                                     modeScope,
                                                     CancellationToken.None);
                }
                catch(Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
                await TryConfirmVersionRemovalAndDeleteLoggedGridFsAsync(libraryId,
                                                                          importedVersion,
                                                                          modeScope,
                                                                          log,
                                                                          cleanupFailures);
            }

            await TryReconcileOwnedCatalogsAfterVersionCleanupAsync(modeScope.Lease,
                                                                     rollbackVersions,
                                                                     rollbackVersions.Select(version =>
                                                                         versionWriteLogs[version]),
                                                                     cleanupFailures);

            foreach(AttemptedReembedJob attempted in attemptedReembedJobs.Where(job =>
                         !preservedReplacements.Contains(job.Version)))
            {
                try
                {
                    CancellationToken ownershipToken = modeScope.Lease.OwnershipLostToken;
                    await RequireActiveModeLeaseAsync(modeScope.Lease, ownershipToken);
                    await mJobRepository.DeleteAsync(attempted.Record.Id, ownershipToken);
                }
                catch(Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }

            try
            {
                await ReconcilePublicationAfterDestructiveAttemptsAsync(libraryId,
                                                                         packageLibrary,
                                                                         previousLibrary,
                                                                         rollbackVersions.ToHashSet(
                                                                             StringComparer.Ordinal),
                                                                         modeScope);
            }
            catch(Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    $"Import failed for {libraryId}, and complete rollback also failed.",
                    cleanupFailures.Prepend(importFailure));
            }
        }
    }

    private static async Task ValidateWebImportOwnershipAsync(
        BundleManifest manifest,
        ILibraryIngestionModeRepository modes,
        ILibraryIngestionModeLease lease,
        ImportModeScope scope)
    {
        LibraryIngestionDataEvidence evidence = await modes.GetLibraryDataEvidenceAsync(
                                                    manifest.Library.Id,
                                                    scope.Token);
        scope.OwnsModeReservation = lease.OwnershipStateAtAcquisition ==
                                    LibraryIngestionOwnershipState.Reserved;
        scope.OwnsNewLibraryData = scope.OwnsModeReservation && !evidence.HasAnyData;
        if (evidence.HasDirectoryDefinition)
        {
            bool reconciled = !scope.OwnsModeReservation ||
                              await lease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory,
                                                                         scope.Token);
            if (!reconciled)
                throw new InvalidOperationException("The package import lost its ingestion-mode lease.");
            if (scope.OwnsModeReservation)
                scope.MarkOwnershipCommitted();
            throw new InvalidOperationException(
                "The library is owned by directory ingestion; web package import was not started.");
        }

        bool hasUnclassifiedPartialData = !evidence.HasLibraryRecord &&
                                          (evidence.HasDocumentLifecycleData ||
                                           evidence.HasChildContentData);
        if (hasUnclassifiedPartialData)
            throw new InvalidOperationException(
                "The library has unclassified partial data; web package import was not started.");
    }

    private async Task ConfigureDirectoryImportAsync(BundleManifest manifest,
                                                      ILibraryIngestionModeRepository modes,
                                                      ILibraryIngestionModeLease lease,
                                                      ImportModeScope scope)
    {
        ISourceDocumentRepository sources = mSourceDocumentRepository ??
                                            throw new InvalidOperationException(
                                                "Directory-library import requires document repositories.");
        DirectoryLibraryDefinition? definition =
            await sources.GetDirectoryDefinitionAsync(manifest.Library.Id, scope.Token);
        scope.ExistingDirectoryDefinition = definition;
        scope.OwnsModeReservation = lease.OwnershipStateAtAcquisition ==
                                    LibraryIngestionOwnershipState.Reserved;
        scope.OwnsNewLibraryData = scope.OwnsModeReservation && definition == null;
        bool ambiguousExistingData = scope.OwnsNewLibraryData &&
                                      await modes.HasAnyLibraryDataAsync(manifest.Library.Id,
                                                                         scope.Token);
        if (ambiguousExistingData)
            throw new InvalidOperationException(
                "Existing library data has no durable ingestion-mode owner; import refused without changing it.");
        if (definition != null)
        {
            IDirectoryPublicationLease? publicationLease =
                await sources.TryAcquireDirectoryPublicationLeaseAsync(
                    manifest.Library.Id,
                    definition.RegistrationRevision,
                    definition.RegistrationIncarnationId,
                    $"package-import-{Guid.NewGuid():N}",
                    definition.LastPublishedVersion,
                    scope.Token);
            if (publicationLease == null)
                throw new InvalidOperationException("The directory library is busy; import was not started.");
            scope.AttachPublicationLease(publicationLease);
        }
    }

    private static async Task PrepareImportForWritesAsync(ImportModeScope scope,
                                                           CancellationToken ct)
    {
        bool modeRenewed = await scope.Lease.TryRenewAsync(ct);
        if (!modeRenewed)
            throw new InvalidOperationException("The package import lost its ingestion-mode lease.");
        if (scope.PublicationLease != null)
            await RequireActivePublicationLeaseAsync(scope.PublicationLease, ct);
        bool committed = await scope.Lease.TryCommitAsync(ct);
        if (!committed)
            throw new InvalidOperationException("The package import lost its ingestion-mode lease.");
        scope.MarkOwnershipCommitted();
    }

    private static async Task RequireActivePublicationLeaseAsync(IDirectoryPublicationLease lease,
                                                                  CancellationToken ct)
    {
        if (lease.OwnershipLostToken.IsCancellationRequested || !await lease.TryRenewAsync(ct))
            throw new InvalidOperationException("The directory import lost its publication lease.");
    }

    private static async Task RequireActiveModeLeaseAsync(ILibraryIngestionModeLease lease,
                                                           CancellationToken ct)
    {
        if (lease.OwnershipLostToken.IsCancellationRequested || !await lease.TryRenewAsync(ct))
            throw new InvalidOperationException("The package import lost its ingestion-mode lease.");
    }

    private async Task<long> PurgeVersionAsync(string libraryId,
                                               BundleVersionEntry versionEntry,
                                               string? profile,
                                               ImportModeScope modeScope,
                                               CancellationToken ct)
    {
        // Estimate bytes freed: embedding vectors + page content.
        long estimated = (long) versionEntry.ChunkCount * versionEntry.EmbeddingDimensions * BytesPerFloat
                         + (long) versionEntry.PageCount * EstimatedBytesPerPage;

        ILibraryDeletionService deletionService = RequireDeletionService();
        if (modeScope.PublicationLease != null)
            await deletionService.DeleteScanCandidateUnderLeaseAsync(profile,
                                                                      libraryId,
                                                                      versionEntry.Version,
                                                                      modeScope.PublicationLease,
                                                                      modeScope.Lease,
                                                                      ct);
        else
            await deletionService.DeleteVersionUnderModeLeaseAsync(profile,
                                                                   libraryId,
                                                                   versionEntry.Version,
                                                                   modeScope.Lease,
                                                                   ct);

        return estimated;
    }

    private async Task<LibraryRecord> PublishLibrarySummaryAsync(LibraryRecord bundleLibrary,
                                                                  BundleManifest manifest,
                                                                  IReadOnlyList<string> versionsImported,
                                                                  ImportModeScope modeScope,
                                                                  CancellationToken ct)
    {
        LibraryRecord? before = await mLibraryRepository.GetLibraryAsync(manifest.Library.Id, ct);
        var allVersions = new HashSet<string>(StringComparer.Ordinal);
        if (before is not null)
        {
            foreach (var v in before.AllVersions)
                allVersions.Add(v);
        }
        foreach (var v in versionsImported)
            allVersions.Add(v);

        // Bundle's claimed CurrentVersion wins when it is among the imported versions.
        var newCurrent = before?.CurrentVersion ?? versionsImported[versionsImported.Count - 1];
        if (versionsImported.Contains(bundleLibrary.CurrentVersion))
            newCurrent = bundleLibrary.CurrentVersion;

        var updated = new LibraryRecord
                          {
                              Id = manifest.Library.Id,
                              Name = manifest.Library.Name,
                              Hint = manifest.Library.Hint,
                              CurrentVersion = newCurrent,
                              AllVersions = allVersions.OrderBy(v => v, StringComparer.Ordinal).ToList()
                          };

        try
        {
            await mLibraryRepository.UpsertLibraryAsync(updated, ct);
        }
        catch(Exception writeFailure)
        {
            LibraryRecord? current;
            try
            {
                current = await mLibraryRepository.GetLibraryAsync(manifest.Library.Id,
                                                                    CancellationToken.None);
            }
            catch(Exception confirmationFailure)
            {
                modeScope.MarkPublicationOutcomeUnknown();
                throw new ImportPublicationOutcomeUnknownException(
                    "Library summary publication failed and its durable outcome could not be confirmed.",
                    new AggregateException(writeFailure, confirmationFailure));
            }

            if (!LibrarySummariesEquivalent(current, updated))
            {
                if (LibrarySummariesEquivalent(current, before))
                    ExceptionDispatchInfo.Capture(writeFailure).Throw();

                modeScope.MarkPublicationOutcomeUnknown();
                throw new ImportPublicationOutcomeUnknownException(
                    "Library summary publication outcome is not attributable to this package import.",
                    writeFailure);
            }
        }

        return updated;
    }

    private async Task WriteVersionAsync(IBundleReader reader,
                                         BundleVersionEntry versionEntry,
                                         ValidatedVersionPackage package,
                                         IReadOnlyDictionary<string, SourceDocumentRecord> packageSources,
                                         IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> packageCatalogs,
                                         bool encoderMatches,
                                         string importOperationId,
                                         VersionWriteLog log,
                                         CancellationToken ct)
    {
        string version = versionEntry.Version;

        // 1. Keep the version private until every dependent store is complete.
        LibraryVersionRecord versionRecord = package.VersionRecord;
        LibraryVersionRecord buildingVersion = versionRecord with
                                                   {
                                                       PublicationState = VersionPublicationState.Building,
                                                       PublicationError = null,
                                                       CleanupInProgress = false,
                                                       ImportOperationId = importOperationId
                                                   };
        bool claimed;
        try
        {
            claimed = await mLibraryRepository.TryClaimImportVersionAsync(buildingVersion,
                                                                           importOperationId,
                                                                           ct);
        }
        catch(Exception claimFailure)
        {
            await ConfirmImportVersionClaimAsync(buildingVersion, log, claimFailure);
            throw;
        }
        if (!claimed)
            throw new InvalidOperationException($"Version '{version}' changed after package preflight.");
        log.VersionClaimed = true;
        log.VersionId = versionRecord.Id;

        await WriteDocumentRecordsAsync(reader,
                                        package,
                                        packageSources,
                                        packageCatalogs,
                                        importOperationId,
                                        log,
                                        ct);

        // 2. Profile (optional).
        if (package.Profile != null)
        {
            await mProfileRepository.UpsertAsync(package.Profile, ct);
            log.ProfileId = package.Profile.Id;
        }

        // 3. Index (optional).
        if (package.Index != null)
        {
            await mIndexRepository.UpsertAsync(package.Index, ct);
            log.IndexId = package.Index.Id;
        }

        // 4. VersionDiff (optional).
        if (package.Diff != null)
        {
            await mDiffRepository.UpsertDiffAsync(package.Diff, ct);
            log.DiffWritten = true;
        }

        // 5. ExcludedSymbols.jsonl.
        await WriteExcludedSymbolsAsync(package.ExcludedSymbols, log, ct);

        // 6. Pages.jsonl.
        await WritePagesAsync(package.Pages, log, ct);

        // 7/8. Chunks — encoder-match attaches embeddings; mismatch leaves Embedding null.
        if (package.Chunks.Count > 0)
        {
            await WriteChunksAsync(reader, version, package.Chunks, versionEntry.EmbeddingDimensions,
                                   encoderMatches, log, ct);
        }

        // 9. BM25 shards — re-upload GridFS blobs then insert shards with rewritten refs.
        await ImportBm25Async(reader, package, log, ct);

        await PublishOwnedSubjectCatalogsAsync(log, ct);
        await PublishImportedVersionAsync(versionRecord, importOperationId, ct);
    }

    private async Task ConfirmImportVersionClaimAsync(LibraryVersionRecord buildingVersion,
                                                       VersionWriteLog log,
                                                       Exception claimFailure)
    {
        LibraryVersionRecord? current;
        try
        {
            current = await mLibraryRepository.GetVersionAsync(buildingVersion.LibraryId,
                                                                buildingVersion.Version,
                                                                CancellationToken.None);
        }
        catch(Exception confirmationFailure)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Version claim failed and its durable outcome could not be confirmed.",
                new AggregateException(claimFailure, confirmationFailure));
        }

        if (current != null)
        {
            bool exactOwnedBuilding = current.PublicationState == VersionPublicationState.Building &&
                                      string.Equals(current.ImportOperationId,
                                                    buildingVersion.ImportOperationId,
                                                    StringComparison.Ordinal) &&
                                      ImportVersionPayloadEquivalent(current, buildingVersion);
            if (!exactOwnedBuilding)
            {
                throw new ImportPublicationOutcomeUnknownException(
                    "Version claim outcome is not attributable to this package import.",
                    claimFailure);
            }
            log.VersionClaimed = true;
            log.VersionId = buildingVersion.Id;
        }
    }

    private async Task WriteDocumentRecordsAsync(
        IBundleReader reader,
        ValidatedVersionPackage package,
        IReadOnlyDictionary<string, SourceDocumentRecord> packageSources,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> packageCatalogs,
        string importOperationId,
        VersionWriteLog log,
        CancellationToken ct)
    {
        if (mSourceDocumentRepository != null &&
            mSubjectCatalogRepository != null &&
            mSubjectAssignmentRepository != null)
        {
            foreach(DocumentRevisionRecord revision in package.DocumentRevisions)
            {
                SourceDocumentRecord source = packageSources[revision.DocumentId];
                await mSourceDocumentRepository.GetOrCreateDocumentAsync(source, ct);
                byte[] original = await ReadEntryBytesAsync(reader,
                                                            BundlePaths.DocumentArtifact(
                                                                revision.OriginalArtifactHash),
                                                            ct);
                byte[]? extraction = string.IsNullOrWhiteSpace(revision.ExtractionArtifactHash)
                                         ? null
                                         : await ReadEntryBytesAsync(
                                               reader,
                                               BundlePaths.DocumentArtifact(revision.ExtractionArtifactHash),
                                               ct);
                await using var originalStream = new MemoryStream(original, writable: false);
                await using var extractionStream = extraction == null
                                                       ? null
                                                       : new MemoryStream(extraction, writable: false);
                await mSourceDocumentRepository.PersistRevisionAsync(revision,
                                                                      originalStream,
                                                                      extractionStream,
                                                                      ct);
                log.ScanRunIds.Add(revision.ScanRunId);
            }

            SubjectCatalogRecord? versionCatalog = GetVersionSubjectCatalog(package.VersionRecord,
                                                                             packageCatalogs);
            if (versionCatalog != null)
                await EnsureSubjectCatalogAsync(mSubjectCatalogRepository,
                                                versionCatalog,
                                                importOperationId,
                                                log,
                                                ct);

            foreach(SubjectAssignmentRecord assignment in package.SubjectAssignments)
            {
                await mSubjectAssignmentRepository.PersistAsync(assignment, ct);
                log.ScanRunIds.Add(assignment.ScanRunId);
            }
        }
    }

    private static SubjectCatalogRecord? GetVersionSubjectCatalog(
        LibraryVersionRecord versionRecord,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> packageCatalogs)
    {
        SubjectCatalogRecord? result = null;
        if (!string.IsNullOrWhiteSpace(versionRecord.SubjectTaxonomyVersion))
        {
            var key = new SubjectCatalogKey(versionRecord.LibraryId, versionRecord.SubjectTaxonomyVersion);
            result = packageCatalogs[key];
        }
        return result;
    }

    private static async Task EnsureSubjectCatalogAsync(ISubjectCatalogRepository catalogs,
                                                        SubjectCatalogRecord catalog,
                                                        string importOperationId,
                                                        VersionWriteLog log,
                                                        CancellationToken ct)
    {
        SubjectCatalogRecord? existing = await catalogs.GetAsync(catalog.LibraryId,
                                                                  catalog.TaxonomyVersion,
                                                                  ct);
        if (existing == null)
        {
            if (string.IsNullOrWhiteSpace(catalog.ScanRunId))
                throw new InvalidDataException("Manifest-v2 subject catalogs require scan ownership.");
            SubjectCatalogRecord candidate = catalog with
                                                 {
                                                     PublicationState =
                                                         SubjectCatalogPublicationState.Candidate,
                                                     ImportOperationId = importOperationId
            };
            log.OwnedCatalogs.Add(new OwnedImportCatalog(candidate, importOperationId));
            try
            {
                await catalogs.InsertRevisionAsync(candidate, ct);
            }
            catch(Exception insertionFailure)
            {
                await ConfirmImportCatalogCandidateInsertAsync(catalogs, candidate, insertionFailure);
                throw;
            }
            log.CatalogScanRunIds.Add(catalog.ScanRunId);
        }
        if (existing != null && !SubjectCatalogsEquivalent(existing, catalog))
        {
            throw new InvalidDataException(
                $"Subject catalog '{catalog.TaxonomyVersion}' conflicts with the receiver's immutable catalog.");
        }
    }

    private static async Task ConfirmImportCatalogCandidateInsertAsync(
        ISubjectCatalogRepository catalogs,
        SubjectCatalogRecord candidate,
        Exception insertionFailure)
    {
        SubjectCatalogRecord? current;
        try
        {
            current = await catalogs.GetAsync(candidate.LibraryId,
                                              candidate.TaxonomyVersion,
                                              CancellationToken.None);
        }
        catch(Exception confirmationFailure)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Subject catalog insertion failed and its durable outcome could not be confirmed.",
                new AggregateException(insertionFailure, confirmationFailure));
        }

        if (current != null)
        {
            bool exactOwnedCandidate = current.PublicationState ==
                                       SubjectCatalogPublicationState.Candidate &&
                                       string.Equals(current.ImportOperationId,
                                                     candidate.ImportOperationId,
                                                     StringComparison.Ordinal) &&
                                       SubjectCatalogsEquivalent(current, candidate);
            if (!exactOwnedCandidate)
            {
                throw new ImportPublicationOutcomeUnknownException(
                    "Subject catalog insertion outcome is not attributable to this package import.",
                    insertionFailure);
            }
        }
    }

    private async Task PublishOwnedSubjectCatalogsAsync(VersionWriteLog log, CancellationToken ct)
    {
        if (log.OwnedCatalogs.Count > 0)
        {
            ISubjectCatalogRepository catalogs = mSubjectCatalogRepository ??
                                                  throw new InvalidOperationException(
                                                      "Subject catalog publication requires its repository.");
            foreach(OwnedImportCatalog ownedCatalog in log.OwnedCatalogs)
                await PublishOwnedSubjectCatalogAsync(catalogs, ownedCatalog, ct);
        }
    }

    private static async Task PublishOwnedSubjectCatalogAsync(ISubjectCatalogRepository catalogs,
                                                               OwnedImportCatalog ownedCatalog,
                                                               CancellationToken ct)
    {
        bool published;
        try
        {
            published = await catalogs.TryPublishImportCandidateAsync(ownedCatalog.Candidate.LibraryId,
                                                                        ownedCatalog.Candidate.TaxonomyVersion,
                                                                        ownedCatalog.ImportOperationId,
                                                                        ct);
        }
        catch(Exception publicationFailure)
        {
            bool committed = await ConfirmImportedCatalogPublicationAsync(catalogs,
                                                                           ownedCatalog,
                                                                           publicationFailure);
            if (!committed)
                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
            published = true;
        }

        if (!published)
        {
            bool committed = await ConfirmImportedCatalogPublicationAsync(
                                 catalogs,
                                 ownedCatalog,
                                 new InvalidOperationException("Subject catalog publication was not applied."));
            if (!committed)
            {
                throw new InvalidOperationException(
                    $"Subject catalog '{ownedCatalog.Candidate.TaxonomyVersion}' was not published.");
            }
        }
    }

    private static async Task<bool> ConfirmImportedCatalogPublicationAsync(
        ISubjectCatalogRepository catalogs,
        OwnedImportCatalog ownedCatalog,
        Exception publicationFailure)
    {
        SubjectCatalogRecord? current;
        try
        {
            current = await catalogs.GetAsync(ownedCatalog.Candidate.LibraryId,
                                              ownedCatalog.Candidate.TaxonomyVersion,
                                              CancellationToken.None);
        }
        catch(Exception confirmationFailure)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Subject catalog publication failed and its durable outcome could not be confirmed.",
                new AggregateException(publicationFailure, confirmationFailure));
        }

        SubjectCatalogRecord expectedPublished = ownedCatalog.Candidate with
                                                     {
                                                         PublicationState =
                                                             SubjectCatalogPublicationState.Published
                                                     };
        bool committed = current != null &&
                         string.Equals(current.ImportOperationId,
                                       ownedCatalog.ImportOperationId,
                                       StringComparison.Ordinal) &&
                         SubjectCatalogsEquivalent(current, expectedPublished);
        bool stillOwnedCandidate = current != null &&
                                   current.PublicationState == SubjectCatalogPublicationState.Candidate &&
                                   string.Equals(current.ImportOperationId,
                                                 ownedCatalog.ImportOperationId,
                                                 StringComparison.Ordinal) &&
                                   SubjectCatalogsEquivalent(
                                       current with { PublicationState = SubjectCatalogPublicationState.Published },
                                       expectedPublished);
        if (!committed && !stillOwnedCandidate)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Subject catalog publication outcome is not attributable to this package import.",
                publicationFailure);
        }

        return committed;
    }

    private async Task PublishImportedVersionAsync(LibraryVersionRecord packageVersion,
                                                    string importOperationId,
                                                    CancellationToken ct)
    {
        LibraryVersionRecord publishedVersion = packageVersion with { ImportOperationId = importOperationId };
        bool published;
        try
        {
            published = await mLibraryRepository.TryPublishImportVersionAsync(publishedVersion,
                                                                               importOperationId,
                                                                               ct);
        }
        catch(Exception publicationFailure)
        {
            bool committed = await ConfirmImportedVersionPublicationAsync(publishedVersion,
                                                                           publicationFailure);
            if (!committed)
                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
            published = true;
        }

        if (!published)
        {
            bool committed = await ConfirmImportedVersionPublicationAsync(
                                 publishedVersion,
                                 new InvalidOperationException("Version publication was not applied."));
            if (!committed)
                throw new InvalidOperationException($"Version '{packageVersion.Version}' was not published.");
        }
    }

    private async Task<bool> ConfirmImportedVersionPublicationAsync(LibraryVersionRecord publishedVersion,
                                                                     Exception publicationFailure)
    {
        LibraryVersionRecord? current;
        try
        {
            current = await mLibraryRepository.GetVersionAsync(publishedVersion.LibraryId,
                                                                publishedVersion.Version,
                                                                CancellationToken.None);
        }
        catch(Exception confirmationFailure)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Version publication failed and its durable outcome could not be confirmed.",
                new AggregateException(publicationFailure, confirmationFailure));
        }

        bool ownerMatches = current != null &&
                            string.Equals(current.ImportOperationId,
                                          publishedVersion.ImportOperationId,
                                          StringComparison.Ordinal);
        bool committed = false;
        bool stillOwnedBuilding = false;
        if (current != null && ownerMatches)
        {
            committed = current.PublicationState == VersionPublicationState.Published &&
                        ImportVersionPayloadEquivalent(current, publishedVersion);
            stillOwnedBuilding = current.PublicationState == VersionPublicationState.Building &&
                                 ImportVersionPayloadEquivalent(
                                     current with { PublicationState = VersionPublicationState.Published },
                                     publishedVersion);
        }
        if (!committed && !stillOwnedBuilding)
        {
            throw new ImportPublicationOutcomeUnknownException(
                "Version publication outcome is not attributable to this package import.",
                publicationFailure);
        }

        return committed;
    }

    private static bool ImportVersionPayloadEquivalent(LibraryVersionRecord left,
                                                       LibraryVersionRecord right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.LibraryId, right.LibraryId, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        left.ScrapedAt == right.ScrapedAt &&
        left.PageCount == right.PageCount &&
        left.ChunkCount == right.ChunkCount &&
        string.Equals(left.EmbeddingProviderId, right.EmbeddingProviderId, StringComparison.Ordinal) &&
        string.Equals(left.EmbeddingModelName, right.EmbeddingModelName, StringComparison.Ordinal) &&
        left.EmbeddingDimensions == right.EmbeddingDimensions &&
        string.Equals(left.ClassifierBackend, right.ClassifierBackend, StringComparison.Ordinal) &&
        string.Equals(left.ClassifierModel, right.ClassifierModel, StringComparison.Ordinal) &&
        string.Equals(left.SubjectTaxonomyVersion, right.SubjectTaxonomyVersion, StringComparison.Ordinal) &&
        string.Equals(left.PreviousVersion, right.PreviousVersion, StringComparison.Ordinal) &&
        left.BoundaryIssuePct.Equals(right.BoundaryIssuePct) &&
        left.Suspect == right.Suspect &&
        left.SuspectReasons.SequenceEqual(right.SuspectReasons, StringComparer.Ordinal) &&
        left.LastSuspectEvaluatedAt == right.LastSuspectEvaluatedAt &&
        string.Equals(left.ScanRunId, right.ScanRunId, StringComparison.Ordinal) &&
        left.RegistrationRevision == right.RegistrationRevision &&
        left.CleanupInProgress == right.CleanupInProgress &&
        string.Equals(left.PublicationError, right.PublicationError, StringComparison.Ordinal) &&
        string.Equals(left.ImportOperationId, right.ImportOperationId, StringComparison.Ordinal);

    private async Task ValidateExistingSubjectCatalogsAsync(
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> packageCatalogs,
        CancellationToken ct)
    {
        if (packageCatalogs.Count > 0 && mSubjectCatalogRepository != null)
        {
            IReadOnlyList<SubjectCatalogRecord> existingCatalogs =
                await mSubjectCatalogRepository.GetManyAsync(packageCatalogs.Keys.ToArray(), ct);
            foreach(SubjectCatalogRecord existing in existingCatalogs)
            {
                var key = new SubjectCatalogKey(existing.LibraryId, existing.TaxonomyVersion);
                if (!packageCatalogs.TryGetValue(key, out SubjectCatalogRecord? packageCatalog) ||
                    !SubjectCatalogsEquivalent(existing, packageCatalog))
                {
                    throw new InvalidDataException(
                        $"Subject catalog '{existing.TaxonomyVersion}' conflicts with the receiver's immutable catalog.");
                }
            }
        }
    }

    private static bool SubjectCatalogsEquivalent(SubjectCatalogRecord left, SubjectCatalogRecord right)
    {
        bool result = left.PublicationState == right.PublicationState &&
                      string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
                      string.Equals(left.LibraryId, right.LibraryId, StringComparison.Ordinal) &&
                      left.Revision == right.Revision &&
                      string.Equals(left.TaxonomyVersion, right.TaxonomyVersion, StringComparison.Ordinal) &&
                      string.Equals(left.ScanRunId, right.ScanRunId, StringComparison.Ordinal) &&
                      string.Equals(left.PreviousTaxonomyVersion,
                                    right.PreviousTaxonomyVersion,
                                    StringComparison.Ordinal) &&
                      left.CreatedAtUtc == right.CreatedAtUtc &&
                      left.Provenance == right.Provenance &&
                      SubjectConceptsEquivalent(left.Concepts, right.Concepts);
        return result;
    }

    private static bool SubjectConceptsEquivalent(IReadOnlyList<SubjectConcept> left,
                                                  IReadOnlyList<SubjectConcept> right)
    {
        bool result = left.Count == right.Count;
        for (int index = 0; result && index < left.Count; index++)
        {
            SubjectConcept leftConcept = left[index];
            SubjectConcept rightConcept = right[index];
            result = string.Equals(leftConcept.Id, rightConcept.Id, StringComparison.Ordinal) &&
                     string.Equals(leftConcept.Label, rightConcept.Label, StringComparison.Ordinal) &&
                     string.Equals(leftConcept.Description, rightConcept.Description, StringComparison.Ordinal) &&
                     leftConcept.Aliases.SequenceEqual(rightConcept.Aliases, StringComparer.Ordinal);
        }

        return result;
    }

    private async Task WriteExcludedSymbolsAsync(IReadOnlyList<ExcludedSymbol> symbols,
                                                  VersionWriteLog log,
                                                  CancellationToken ct)
    {
        var batch = new List<ExcludedSymbol>();
        foreach(ExcludedSymbol symbol in symbols)
        {
            batch.Add(symbol);
            log.ExcludedIds.Add(symbol.Id);
        }
        if (batch.Count > 0)
            await mExcludedSymbolsRepository.UpsertManyAsync(batch, ct);
    }

    private async Task WritePagesAsync(IReadOnlyList<PageRecord> pages,
                                       VersionWriteLog log,
                                       CancellationToken ct)
    {
        var batch = new List<PageRecord>(PageBatchSize);
        foreach(PageRecord page in pages)
        {
            batch.Add(page);
            log.PageIds.Add(page.Id);
            if (batch.Count >= PageBatchSize)
            {
                await FlushPageBatchAsync(batch, ct);
            }
        }
        if (batch.Count > 0)
            await FlushPageBatchAsync(batch, ct);
    }

    private async Task FlushPageBatchAsync(List<PageRecord> batch, CancellationToken ct)
    {
        foreach (var page in batch)
            await mPageRepository.UpsertPageAsync(page, ct);
        batch.Clear();
    }

    private async Task WriteChunksAsync(IBundleReader reader,
                                         string version,
                                         IReadOnlyList<DocChunk> chunks,
                                         int dim,
                                         bool encoderMatches,
                                         VersionWriteLog log,
                                         CancellationToken ct)
    {
        if (encoderMatches)
            await AttachAndInsertChunksAsync(reader, version, chunks, dim, log, ct);
        else
            await InsertChunksWithNullEmbeddingsAsync(chunks, log, ct);
    }

    private async Task AttachAndInsertChunksAsync(IBundleReader reader,
                                                   string version,
                                                   IReadOnlyList<DocChunk> chunks,
                                                   int dim,
                                                   VersionWriteLog log,
                                                   CancellationToken ct)
    {
        var embedPath = BundlePaths.VersionFilePath(version, BundlePaths.EmbeddingsBlobFile);
        await using var embedStream = reader.OpenEntry(embedPath);
        var embedReader = new EmbeddingBlobReader(embedStream, dim);

        var batch = new List<DocChunk>(ChunkBatchSize);
        foreach (var chunk in chunks)
        {
            var embedding = await embedReader.ReadAsync(ct);
            var withEmbed = chunk with { Embedding = embedding };
            batch.Add(withEmbed);
            log.ChunkIds.Add(withEmbed.Id);
            if (batch.Count >= ChunkBatchSize)
            {
                await mChunkRepository.InsertChunksAsync(batch, ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            await mChunkRepository.InsertChunksAsync(batch, ct);
    }

    private async Task InsertChunksWithNullEmbeddingsAsync(IReadOnlyList<DocChunk> chunks,
                                                            VersionWriteLog log,
                                                            CancellationToken ct)
    {
        var batch = new List<DocChunk>(ChunkBatchSize);
        foreach (var chunk in chunks)
        {
            // Encoder mismatch: store chunk with Embedding = null.
            // Task 19 will enqueue a reembed job.
            var withNull = chunk with { Embedding = null };
            batch.Add(withNull);
            log.ChunkIds.Add(withNull.Id);
            if (batch.Count >= ChunkBatchSize)
            {
                await mChunkRepository.InsertChunksAsync(batch, ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            await mChunkRepository.InsertChunksAsync(batch, ct);
    }

    private async Task ImportBm25Async(IBundleReader reader,
                                        ValidatedVersionPackage package,
                                        VersionWriteLog log,
                                        CancellationToken ct)
    {
        if (package.Bm25Shards.Count > 0)
        {
            var idMap = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach(string entryPath in package.Bm25GridFsPaths)
            {
                var originalId = Path.GetFileNameWithoutExtension(entryPath);
                await using var src = reader.OpenEntry(entryPath);
                string newId = ObjectId.GenerateNewId().ToString();
                log.GridFsIds.Add(newId);
                await mBm25Repository.UploadGridFsBlobAsync(newId, src, ct);
                idMap[originalId] = newId;
            }

            foreach(Bm25Shard shard in package.Bm25Shards)
            {
                var rewritten = RewriteShardRefs(shard, idMap);
                await mBm25Repository.UpsertShardAsync(rewritten, ct);
                log.ShardIds.Add(rewritten.Id);
            }
        }
    }

    private static Bm25Shard RewriteShardRefs(Bm25Shard shard, IReadOnlyDictionary<string, string> idMap)
    {
        string? rewrittenWhole = null;
        if (shard.ShardGridFsRef is not null)
        {
            if (!idMap.TryGetValue(shard.ShardGridFsRef, out var newWhole))
                throw new InvalidOperationException(
                    $"Shard references GridFS id {shard.ShardGridFsRef} not present in bundle");
            rewrittenWhole = newWhole;
        }

        var rewrittenExternal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in shard.ExternalTerms)
        {
            if (!idMap.TryGetValue(kv.Value, out var newRef))
                throw new InvalidOperationException(
                    $"Term '{kv.Key}' references GridFS id {kv.Value} not present in bundle");
            rewrittenExternal[kv.Key] = newRef;
        }

        return shard with { ShardGridFsRef = rewrittenWhole, ExternalTerms = rewrittenExternal };
    }

    private async Task TryReconcileOwnedCatalogsAfterVersionCleanupAsync(
        ILibraryIngestionModeLease modeLease,
        IReadOnlyCollection<string> cleanupVersions,
        IEnumerable<VersionWriteLog> logs,
        ICollection<Exception> recoveryFailures)
    {
        ArgumentNullException.ThrowIfNull(cleanupVersions);
        ArgumentNullException.ThrowIfNull(logs);
        if (cleanupVersions.Count > 0)
        {
            await ReconcileOwnedCatalogsAfterVersionCleanupAsync(modeLease,
                                                                  cleanupVersions,
                                                                  logs,
                                                                  recoveryFailures);
        }
    }

    private async Task ReconcileOwnedCatalogsAfterVersionCleanupAsync(
        ILibraryIngestionModeLease modeLease,
        IReadOnlyCollection<string> cleanupVersions,
        IEnumerable<VersionWriteLog> logs,
        ICollection<Exception> recoveryFailures)
    {
        IReadOnlyList<OwnedImportCatalog> ownedCatalogs = logs
            .SelectMany(log => log.OwnedCatalogs)
            .DistinctBy(owned => (owned.Candidate.LibraryId,
                                  owned.Candidate.TaxonomyVersion,
                                  owned.ImportOperationId))
            .ToList();
        if (ownedCatalogs.Count > 0)
        {
            ISubjectCatalogRepository catalogs = mSubjectCatalogRepository ??
                                                   throw new InvalidOperationException(
                                                       "Subject catalog rollback requires its repository.");
            foreach(OwnedImportCatalog ownedCatalog in ownedCatalogs)
            {
                try
                {
                    await ReconcileOwnedCatalogAfterVersionCleanupAsync(modeLease,
                                                                        catalogs,
                                                                        ownedCatalog,
                                                                        cleanupVersions.First());
                }
                catch(Exception rollbackFailure)
                {
                    recoveryFailures.Add(rollbackFailure);
                }
            }
        }
    }

    private static async Task ReconcileOwnedCatalogAfterVersionCleanupAsync(
        ILibraryIngestionModeLease modeLease,
        ISubjectCatalogRepository catalogs,
        OwnedImportCatalog ownedCatalog,
        string cleanupVersion)
    {
        CancellationToken ownershipToken = modeLease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeLease, ownershipToken);
        ImportCatalogRollbackOutcome outcome =
            await catalogs.TryRollbackImportCandidatePublicationIfUnreferencedAsync(
                ownedCatalog.Candidate.LibraryId,
                ownedCatalog.Candidate.TaxonomyVersion,
                ownedCatalog.ImportOperationId,
                ownershipToken);
        bool deleteCandidate = outcome != ImportCatalogRollbackOutcome.ReferencedBySurvivor &&
                               outcome != ImportCatalogRollbackOutcome.NotOwned;
        if (outcome == ImportCatalogRollbackOutcome.NotOwned)
        {
            SubjectCatalogRecord? current = await catalogs.GetAsync(ownedCatalog.Candidate.LibraryId,
                                                                     ownedCatalog.Candidate.TaxonomyVersion,
                                                                     ownershipToken);
            if (current != null)
            {
                throw new InvalidOperationException(
                    $"Subject catalog '{ownedCatalog.Candidate.TaxonomyVersion}' changed ownership during rollback.");
            }
        }

        if (deleteCandidate)
        {
            await DeleteOwnedCatalogCandidateAsync(modeLease,
                                                    catalogs,
                                                    ownedCatalog,
                                                    cleanupVersion);
        }
    }

    private static async Task DeleteOwnedCatalogCandidateAsync(
        ILibraryIngestionModeLease modeLease,
        ISubjectCatalogRepository catalogs,
        OwnedImportCatalog ownedCatalog,
        string failedVersion)
    {
        CancellationToken ownershipToken = modeLease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeLease, ownershipToken);
        bool deleted = await catalogs.DeleteImportCandidateIfUnreferencedAsync(
                           ownedCatalog.Candidate.LibraryId,
                           ownedCatalog.Candidate.TaxonomyVersion,
                           ownedCatalog.ImportOperationId,
                           failedVersion,
                           ownershipToken);
        if (!deleted)
        {
            SubjectCatalogRecord? current = await catalogs.GetAsync(ownedCatalog.Candidate.LibraryId,
                                                                     ownedCatalog.Candidate.TaxonomyVersion,
                                                                     ownershipToken);
            bool importerOwnedCatalogRemains = current != null &&
                string.Equals(current.ImportOperationId,
                              ownedCatalog.ImportOperationId,
                              StringComparison.Ordinal);
            if (importerOwnedCatalogRemains)
            {
                throw new InvalidOperationException(
                    $"Subject catalog '{ownedCatalog.Candidate.TaxonomyVersion}' remained after import rollback.");
            }
        }
    }

    private async Task RollbackAndReconcileFailedVersionAsync(
        string libraryId,
        string failedVersion,
        string? profile,
        ImportModeScope modeScope,
        BundleLibraryInfo packageLibrary,
        LibraryRecord? previousLibrary,
        IReadOnlyCollection<string> overwrittenVersions,
        VersionWriteLog log,
        Exception importFailure)
    {
        var recoveryFailures = new List<Exception>();
        if (log.VersionClaimed)
        {
            try
            {
                await DeleteImportedVersionAsync(libraryId,
                                                 failedVersion,
                                                 profile,
                                                 modeScope,
                                                 CancellationToken.None);
            }
            catch(Exception rollbackFailure)
            {
                recoveryFailures.Add(rollbackFailure);
            }
        }

        await TryConfirmVersionRemovalAndDeleteLoggedGridFsAsync(libraryId,
                                                                  failedVersion,
                                                                  modeScope,
                                                                  log,
                                                                  recoveryFailures);
        await TryReconcileOwnedCatalogsAfterVersionCleanupAsync(modeScope.Lease,
                                                                 [failedVersion],
                                                                 [log],
                                                                 recoveryFailures);

        if (overwrittenVersions.Contains(failedVersion, StringComparer.Ordinal))
        {
            try
            {
                await ReconcileFailedOverwritePublicationAsync(libraryId,
                                                                failedVersion,
                                                                packageLibrary,
                                                                previousLibrary,
                                                                modeScope);
            }
            catch(Exception reconciliationFailure)
            {
                recoveryFailures.Add(reconciliationFailure);
            }
        }

        if (recoveryFailures.Count > 0)
        {
            throw new AggregateException(
                $"Import failed for {libraryId}/{failedVersion}, and failure recovery also failed.",
                recoveryFailures.Prepend(importFailure));
        }
    }

    private async Task DeleteImportedVersionAsync(string libraryId,
                                                   string version,
                                                   string? profile,
                                                   ImportModeScope modeScope,
                                                  CancellationToken ct)
    {
        ILibraryDeletionService deletionService = RequireDeletionService();
        if (modeScope.PublicationLease != null)
        {
            await deletionService.DeleteScanCandidateUnderLeaseAsync(profile,
                                                                      libraryId,
                                                                      version,
                                                                      modeScope.PublicationLease,
                                                                      modeScope.Lease,
                                                                      ct);
        }
        else
        {
            await deletionService.DeleteVersionUnderModeLeaseAsync(profile,
                                                                    libraryId,
                                                                    version,
                                                                    modeScope.Lease,
                                                                    ct);
        }
    }

    private async Task TryConfirmVersionRemovalAndDeleteLoggedGridFsAsync(
        string libraryId,
        string version,
        ImportModeScope modeScope,
        VersionWriteLog log,
        ICollection<Exception> recoveryFailures)
    {
        bool versionRemoved = false;
        try
        {
            using CancellationTokenSource ownership = CreateCleanupOwnershipTokenSource(modeScope);
            await RequireActiveImportCleanupLeasesAsync(modeScope, ownership.Token);
            LibraryVersionRecord? current = await mLibraryRepository.GetVersionAsync(libraryId,
                                                                                      version,
                                                                                      ownership.Token);
            if (current != null)
            {
                recoveryFailures.Add(new InvalidOperationException(
                    $"Imported version '{libraryId}/{version}' remained after cleanup; its GridFS blobs were preserved."));
            }
            versionRemoved = current == null;
        }
        catch(Exception confirmationFailure)
        {
            recoveryFailures.Add(confirmationFailure);
        }

        if (versionRemoved)
        {
            foreach(string gridFsId in log.GridFsIds.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    using CancellationTokenSource ownership = CreateCleanupOwnershipTokenSource(modeScope);
                    await RequireActiveImportCleanupLeasesAsync(modeScope, ownership.Token);
                    await mBm25Repository.DeleteGridFsBlobAsync(gridFsId, ownership.Token);
                }
                catch(Exception cleanupFailure)
                {
                    recoveryFailures.Add(cleanupFailure);
                }
            }
        }
    }

    private static CancellationTokenSource CreateCleanupOwnershipTokenSource(ImportModeScope modeScope) =>
        modeScope.PublicationLease == null
            ? CancellationTokenSource.CreateLinkedTokenSource(modeScope.Lease.OwnershipLostToken)
            : CancellationTokenSource.CreateLinkedTokenSource(modeScope.Lease.OwnershipLostToken,
                                                               modeScope.PublicationLease.OwnershipLostToken);

    private static async Task RequireActiveImportCleanupLeasesAsync(ImportModeScope modeScope,
                                                                     CancellationToken ct)
    {
        await RequireActiveModeLeaseAsync(modeScope.Lease, ct);
        if (modeScope.PublicationLease != null)
            await RequireActivePublicationLeaseAsync(modeScope.PublicationLease, ct);
    }

    private Task ReconcileFailedOverwritePublicationAsync(string libraryId,
                                                           string failedVersion,
                                                           BundleLibraryInfo packageLibrary,
                                                           LibraryRecord? previousLibrary,
                                                           ImportModeScope modeScope) =>
        ReconcilePublicationAfterDestructiveAttemptsAsync(libraryId,
                                                           packageLibrary,
                                                           previousLibrary,
                                                           new HashSet<string>([failedVersion],
                                                                               StringComparer.Ordinal),
                                                           modeScope);

    private async Task ReconcilePublicationAfterDestructiveAttemptsAsync(
        string libraryId,
        BundleLibraryInfo packageLibrary,
        LibraryRecord? previousLibrary,
        IReadOnlySet<string> attemptedVersions,
        ImportModeScope modeScope)
    {
        ArgumentNullException.ThrowIfNull(packageLibrary);
        ArgumentNullException.ThrowIfNull(attemptedVersions);
        if (!string.Equals(packageLibrary.Id, libraryId, StringComparison.Ordinal))
            throw new InvalidOperationException("The package library identity changed during overwrite recovery.");

        CancellationToken ownershipToken = modeScope.Lease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeScope.Lease, ownershipToken);
        IReadOnlyList<LibraryVersionRecord> versionRows =
            await mLibraryRepository.GetVersionsAsync(libraryId, ownershipToken);
        IReadOnlyList<LibraryVersionRecord> survivors = versionRows
            .Where(version => version.PublicationState == VersionPublicationState.Published &&
                              !version.CleanupInProgress &&
                              !attemptedVersions.Contains(version.Version))
            .OrderByDescending(version => version.ScrapedAt)
            .ThenBy(version => version.Version, StringComparer.Ordinal)
            .ToList();
        LibraryVersionRecord? selectedVersion = SelectRecoveryVersion(previousLibrary, survivors);

        var recoveryFailures = new List<Exception>();
        try
        {
            await ReconcileLibrarySummaryAfterDestructiveAttemptsAsync(libraryId,
                                                                        packageLibrary,
                                                                        previousLibrary,
                                                                        survivors,
                                                                        selectedVersion,
                                                                        modeScope.Lease);
        }
        catch(Exception recoveryFailure)
        {
            recoveryFailures.Add(recoveryFailure);
        }

        try
        {
            await ReconcileDirectoryPublicationAfterDestructiveAttemptsAsync(libraryId,
                                                                               selectedVersion,
                                                                               modeScope);
        }
        catch(Exception recoveryFailure)
        {
            recoveryFailures.Add(recoveryFailure);
        }

        if (recoveryFailures.Count > 0)
        {
            throw new AggregateException(
                $"Publication metadata recovery failed for library '{libraryId}'.",
                recoveryFailures);
        }
    }

    private async Task ReconcileLibrarySummaryAfterDestructiveAttemptsAsync(
        string libraryId,
        BundleLibraryInfo packageLibrary,
        LibraryRecord? previousLibrary,
        IReadOnlyList<LibraryVersionRecord> survivors,
        LibraryVersionRecord? selectedVersion,
        ILibraryIngestionModeLease modeLease)
    {
        CancellationToken ownershipToken = modeLease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeLease, ownershipToken);
        LibraryRecord? current = await mLibraryRepository.GetLibraryAsync(libraryId, ownershipToken);
        if (previousLibrary == null)
        {
            ValidateNoUnexpectedLibrarySummary(current);
        }
        else
        {
            await ReconcileExistingLibrarySummaryAsync(libraryId,
                                                       packageLibrary,
                                                       previousLibrary,
                                                       current,
                                                       survivors,
                                                       selectedVersion,
                                                       modeLease);
        }
    }

    private async Task ReconcileExistingLibrarySummaryAsync(
        string libraryId,
        BundleLibraryInfo packageLibrary,
        LibraryRecord previousLibrary,
        LibraryRecord? current,
        IReadOnlyList<LibraryVersionRecord> survivors,
        LibraryVersionRecord? selectedVersion,
        ILibraryIngestionModeLease modeLease)
    {
        if (current == null)
        {
            ValidateMissingLibrarySummary(survivors);
        }
        else
        {
            await ReconcilePresentLibrarySummaryAsync(libraryId,
                                                      packageLibrary,
                                                      previousLibrary,
                                                      current,
                                                      survivors,
                                                      selectedVersion,
                                                      modeLease);
        }
    }

    private async Task ReconcilePresentLibrarySummaryAsync(
        string libraryId,
        BundleLibraryInfo packageLibrary,
        LibraryRecord previousLibrary,
        LibraryRecord current,
        IReadOnlyList<LibraryVersionRecord> survivors,
        LibraryVersionRecord? selectedVersion,
        ILibraryIngestionModeLease modeLease)
    {
        if (survivors.Count == 0)
        {
            await DeleteLibrarySummaryWithConfirmationAsync(current, modeLease);
        }
        else
        {
            LibraryRecord desired = CreateRecoveredLibrarySummary(libraryId,
                                                                   packageLibrary,
                                                                   previousLibrary,
                                                                   survivors,
                                                                   selectedVersion);
            if (!LibrarySummariesEquivalent(current, desired))
                await ReplaceLibrarySummaryWithConfirmationAsync(current, desired, modeLease);
        }
    }

    private static LibraryRecord CreateRecoveredLibrarySummary(
        string libraryId,
        BundleLibraryInfo packageLibrary,
        LibraryRecord previousLibrary,
        IReadOnlyList<LibraryVersionRecord> survivors,
        LibraryVersionRecord? selectedVersion)
    {
        LibraryVersionRecord publishedVersion = selectedVersion ??
                                                throw new InvalidOperationException(
                                                    "Overwrite recovery could not select a published survivor.");
        IReadOnlySet<string> survivorNames = survivors
            .Select(version => version.Version)
            .ToHashSet(StringComparer.Ordinal);
        var orderedVersions = previousLibrary.AllVersions
            .Where(survivorNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        orderedVersions.AddRange(survivorNames
                                     .Where(version => !orderedVersions.Contains(version,
                                                                                  StringComparer.Ordinal))
                                     .Order(StringComparer.Ordinal));
        var result = new LibraryRecord
                         {
                             Id = libraryId,
                             Name = previousLibrary.Name ?? packageLibrary.Name,
                             Hint = previousLibrary.Hint ?? packageLibrary.Hint,
                             CurrentVersion = publishedVersion.Version,
                             AllVersions = orderedVersions
                         };
        return result;
    }

    private static void ValidateNoUnexpectedLibrarySummary(LibraryRecord? current)
    {
        if (current != null)
        {
            throw new InvalidOperationException(
                "Overwrite recovery observed a library summary that did not exist before the purge.");
        }
    }

    private static void ValidateMissingLibrarySummary(IReadOnlyCollection<LibraryVersionRecord> survivors)
    {
        if (survivors.Count > 0)
        {
            throw new InvalidOperationException(
                "Overwrite recovery could not restore a missing summary for surviving published versions.");
        }
    }

    private async Task ReplaceLibrarySummaryWithConfirmationAsync(
        LibraryRecord expected,
        LibraryRecord desired,
        ILibraryIngestionModeLease modeLease)
    {
        CancellationToken ownershipToken = modeLease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeLease, ownershipToken);
        Exception? writeFailure = null;
        bool replaced = false;
        try
        {
            replaced = await mLibraryRepository.TryReplaceLibrarySummaryAsync(expected,
                                                                                desired,
                                                                                ownershipToken);
        }
        catch(Exception ex)
        {
            writeFailure = ex;
        }

        if (!replaced)
        {
            await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
            LibraryRecord? current = await mLibraryRepository.GetLibraryAsync(expected.Id,
                                                                               CancellationToken.None);
            if (!LibrarySummariesEquivalent(current, desired))
            {
                if (LibrarySummariesEquivalent(current, expected))
                {
                    throw writeFailure ?? new InvalidOperationException(
                        "The stale library summary was not replaced during overwrite recovery.");
                }
                throw new InvalidOperationException(
                    "The library summary changed outside the attributable overwrite recovery state.",
                    writeFailure);
            }
        }
    }

    private async Task DeleteLibrarySummaryWithConfirmationAsync(
        LibraryRecord expected,
        ILibraryIngestionModeLease modeLease)
    {
        CancellationToken ownershipToken = modeLease.OwnershipLostToken;
        await RequireActiveModeLeaseAsync(modeLease, ownershipToken);
        Exception? writeFailure = null;
        bool deleted = false;
        try
        {
            deleted = await mLibraryRepository.TryDeleteLibrarySummaryAsync(expected, ownershipToken);
        }
        catch(Exception ex)
        {
            writeFailure = ex;
        }

        if (!deleted)
        {
            await RequireActiveModeLeaseAsync(modeLease, CancellationToken.None);
            LibraryRecord? current = await mLibraryRepository.GetLibraryAsync(expected.Id,
                                                                               CancellationToken.None);
            if (current != null)
            {
                if (LibrarySummariesEquivalent(current, expected))
                {
                    throw writeFailure ?? new InvalidOperationException(
                        "The stale library summary was not deleted during overwrite recovery.");
                }
                throw new InvalidOperationException(
                    "The library summary changed outside the attributable overwrite recovery state.",
                    writeFailure);
            }
        }
    }

    private async Task ReconcileDirectoryPublicationAfterDestructiveAttemptsAsync(
        string libraryId,
        LibraryVersionRecord? selectedVersion,
        ImportModeScope modeScope)
    {
        IDirectoryPublicationLease? publicationLease = modeScope.PublicationLease;
        if (publicationLease != null)
        {
            await ReconcileDirectoryPublicationUnderLeaseAsync(libraryId,
                                                                 selectedVersion,
                                                                 modeScope,
                                                                 publicationLease);
        }
    }

    private async Task ReconcileDirectoryPublicationUnderLeaseAsync(
        string libraryId,
        LibraryVersionRecord? selectedVersion,
        ImportModeScope modeScope,
        IDirectoryPublicationLease publicationLease)
    {
        ISourceDocumentRepository sources = mSourceDocumentRepository ??
                                            throw new InvalidOperationException(
                                                "Directory-library import requires document repositories.");
        await RequireActiveModeLeaseAsync(modeScope.Lease, CancellationToken.None);
        await RequireActivePublicationLeaseAsync(publicationLease, CancellationToken.None);
        DirectoryLibraryDefinition? current = await sources.GetDirectoryDefinitionAsync(
                                                  libraryId,
                                                  CancellationToken.None);
        if (!DirectoryPublicationOwnerMatches(current, publicationLease))
        {
            throw new InvalidOperationException(
                "Directory publication ownership changed during overwrite recovery.");
        }

        string? desiredVersion = selectedVersion?.Version;
        DateTime? desiredPublishedAt = selectedVersion?.ScrapedAt;
        if (!DirectoryPublicationPointerMatches(current, desiredVersion, desiredPublishedAt))
        {
            await UpdateDirectoryPublicationWithConfirmationAsync(libraryId,
                                                                    current?.LastPublishedVersion,
                                                                    desiredVersion,
                                                                    desiredPublishedAt,
                                                                    publicationLease,
                                                                    sources);
        }
    }

    private static async Task UpdateDirectoryPublicationWithConfirmationAsync(
        string libraryId,
        string? expectedLastPublishedVersion,
        string? desiredVersion,
        DateTime? desiredPublishedAt,
        IDirectoryPublicationLease publicationLease,
        ISourceDocumentRepository sources)
    {
        Exception? writeFailure = null;
        bool updated = false;
        try
        {
            updated = await sources.TryUpdateDirectoryPublicationAsync(publicationLease,
                                                                         expectedLastPublishedVersion,
                                                                         desiredPublishedAt,
                                                                         desiredVersion,
                                                                         CancellationToken.None);
        }
        catch(Exception ex)
        {
            writeFailure = ex;
        }

        if (!updated)
        {
            DirectoryLibraryDefinition? confirmed = await sources.GetDirectoryDefinitionAsync(
                                                        libraryId,
                                                        CancellationToken.None);
            bool confirmedMatches = DirectoryPublicationOwnerMatches(confirmed, publicationLease) &&
                                    DirectoryPublicationPointerMatches(confirmed,
                                                                       desiredVersion,
                                                                       desiredPublishedAt);
            if (!confirmedMatches)
            {
                throw new InvalidOperationException(
                    "The directory publication pointer could not be reconciled after overwrite failure.",
                    writeFailure);
            }
        }
    }

    private static LibraryVersionRecord? SelectRecoveryVersion(
        LibraryRecord? previousLibrary,
        IReadOnlyList<LibraryVersionRecord> survivors)
    {
        LibraryVersionRecord? preferred = survivors.FirstOrDefault(version =>
            string.Equals(version.Version, previousLibrary?.CurrentVersion, StringComparison.Ordinal));
        return preferred ?? survivors.FirstOrDefault();
    }

    private static bool LibrarySummariesEquivalent(LibraryRecord? left, LibraryRecord? right) =>
        left == null
            ? right == null
            : right != null &&
              string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
              string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
              string.Equals(left.Hint, right.Hint, StringComparison.Ordinal) &&
              string.Equals(left.CurrentVersion, right.CurrentVersion, StringComparison.Ordinal) &&
              left.AllVersions.SequenceEqual(right.AllVersions, StringComparer.Ordinal);

    private static bool DirectoryPublicationPointerMatches(DirectoryLibraryDefinition? definition,
                                                           string? version,
                                                           DateTime? publishedAtUtc) =>
        definition != null &&
        string.Equals(definition.LastPublishedVersion, version, StringComparison.Ordinal) &&
        definition.LastPublishedAtUtc == publishedAtUtc;

    private async Task DeleteNewLibraryAfterFailureAsync(
        string libraryId,
        string? profile,
        ImportModeScope modeScope,
        IReadOnlyDictionary<string, VersionWriteLog> versionWriteLogs,
        Exception importFailure)
    {
        modeScope.MarkCleanupAttempted();
        var cleanupFailures = new List<Exception>();
        try
        {
            ILibraryDeletionService deletionService = RequireDeletionService();
            await deletionService.DeleteLibraryUnderModeLeaseAsync(profile,
                                                                    libraryId,
                                                                    modeScope.Lease,
                                                                    CancellationToken.None);
        }
        catch(Exception cleanupFailure)
        {
            cleanupFailures.Add(cleanupFailure);
        }

        foreach((string version, VersionWriteLog log) in versionWriteLogs)
        {
            await TryConfirmVersionRemovalAndDeleteLoggedGridFsAsync(libraryId,
                                                                      version,
                                                                      modeScope,
                                                                      log,
                                                                      cleanupFailures);
        }

        if (cleanupFailures.Count == 0)
        {
            try
            {
                await RequireActiveModeLeaseAsync(modeScope.Lease, CancellationToken.None);
                bool ownershipDeleted = await modeScope.Lease.TryDeleteOwnershipAsync(CancellationToken.None);
                if (!ownershipDeleted)
                    throw new InvalidOperationException("The failed import retained its ingestion-mode ownership.");
                modeScope.MarkOwnershipRemoved();
            }
            catch(Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                $"Import failed for {libraryId}, and removal of its new library data also failed.",
                cleanupFailures.Prepend(importFailure));
        }
    }

    private ILibraryDeletionService RequireDeletionService() =>
        mDeletionService ?? throw new InvalidOperationException(
            "Import overwrite and rollback require the shared lifecycle deletion service.");

    private sealed class ImportModeScope : IAsyncDisposable
    {
        internal ImportModeScope(ILibraryIngestionModeLease lease,
                                  CancellationToken requestedToken)
        {
            ArgumentNullException.ThrowIfNull(lease);
            Lease = lease;
            mRequestedToken = requestedToken;
            mOperation = CancellationTokenSource.CreateLinkedTokenSource(requestedToken,
                                                                          lease.OwnershipLostToken);
        }

        private CancellationTokenSource mOperation;
        private readonly CancellationToken mRequestedToken;

        internal ILibraryIngestionModeLease Lease { get; }

        internal CancellationToken Token => mOperation.Token;

        internal Dictionary<string, VersionWriteLog> VersionWriteLogs { get; } =
            new(StringComparer.Ordinal);

        internal IDirectoryPublicationLease? PublicationLease { get; private set; }

        internal DirectoryLibraryDefinition? ExistingDirectoryDefinition { get; set; }

        internal bool OwnsModeReservation { get; set; }

        internal bool OwnsNewLibraryData { get; set; }

        internal bool PublicationEstablished { get; private set; }

        internal bool PublicationOutcomeUnknown { get; private set; }

        internal bool OwnershipCommitted => mOwnershipCommitted;

        internal bool CleanupAttempted { get; private set; }

        private bool mOwnershipCommitted;
        private bool mOwnershipRemoved;

        internal void AttachPublicationLease(IDirectoryPublicationLease publicationLease)
        {
            ArgumentNullException.ThrowIfNull(publicationLease);
            CancellationTokenSource previous = mOperation;
            PublicationLease = publicationLease;
            mOperation = CancellationTokenSource.CreateLinkedTokenSource(mRequestedToken,
                                                                          Lease.OwnershipLostToken,
                                                                          publicationLease.OwnershipLostToken);
            previous.Dispose();
        }

        internal void MarkOwnershipCommitted() => mOwnershipCommitted = true;

        internal void MarkOwnershipRemoved() => mOwnershipRemoved = true;

        internal void MarkPublicationEstablished() => PublicationEstablished = true;

        internal void MarkPublicationOutcomeUnknown() => PublicationOutcomeUnknown = true;

        internal void MarkCleanupAttempted() => CleanupAttempted = true;

        internal void TrackVersionWriteLog(string version, VersionWriteLog log)
        {
            ArgumentException.ThrowIfNullOrEmpty(version);
            ArgumentNullException.ThrowIfNull(log);
            VersionWriteLogs.Add(version, log);
        }

        public async ValueTask DisposeAsync()
        {
            var cleanupFailures = new List<Exception>();
            if (OwnsModeReservation && !mOwnershipCommitted && !mOwnershipRemoved)
            {
                try
                {
                    bool abandoned = await Lease.TryAbandonReservationAsync(CancellationToken.None);
                    if (!abandoned)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            "The failed import retained its ingestion-mode reservation."));
                    }
                }
                catch(Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }

            if (PublicationLease != null)
            {
                try
                {
                    await PublicationLease.DisposeAsync();
                }
                catch(Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }

            try
            {
                mOperation.Dispose();
            }
            catch(Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            try
            {
                await Lease.DisposeAsync();
            }
            catch(Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "One or more package import scope cleanup operations failed.",
                    cleanupFailures);
            }
        }
    }

    private static async Task<T> ReadJsonAsync<T>(IBundleReader reader, string path, CancellationToken ct)
    {
        await using var stream = reader.OpenEntry(path);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, BundleJsonOptions.Default, ct)
                     ?? throw new InvalidOperationException($"'{path}' deserialized to null");
        return result;
    }

    private static Task<IReadOnlyList<T>> ReadTopLevelJsonlAsync<T>(IBundleReader reader,
                                                                    BundleManifest manifest,
                                                                    string path,
                                                                    CancellationToken ct) =>
        manifest.Blobs.ContainsKey(path)
            ? ReadJsonlAsync<T>(reader, path, ct)
            : Task.FromResult<IReadOnlyList<T>>([]);

    private static async Task<IReadOnlyList<T>> ReadJsonlAsync<T>(IBundleReader reader,
                                                                  string path,
                                                                  CancellationToken ct)
    {
        await using Stream stream = reader.OpenEntry(path);
        var jsonl = new JsonlReader<T>(stream);
        var result = new List<T>();
        await foreach(T item in jsonl.ReadAllAsync(ct))
            result.Add(item);
        return result;
    }

    private static async Task<byte[]> ReadEntryBytesAsync(IBundleReader reader,
                                                           string path,
                                                           CancellationToken ct)
    {
        await using Stream stream = reader.OpenEntry(path);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, ct);
        return result.ToArray();
    }

    private static async Task<BundleManifest> ReadManifestAsync(IBundleReader reader, CancellationToken ct)
    {
        if (!reader.HasEntry(BundlePaths.ManifestFile))
            throw new InvalidOperationException("Bundle is missing manifest.json");
        await using var stream = reader.OpenEntry(BundlePaths.ManifestFile);
        var manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, BundleJsonOptions.Default, ct)
                       ?? throw new InvalidOperationException("manifest.json is empty or invalid");
        return manifest;
    }

    private static void ValidateManifestIdentities(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Library == null)
            throw new InvalidDataException("The bundle manifest is missing its library identity.");
        if (manifest.Versions == null)
            throw new InvalidDataException("The bundle manifest is missing its versions collection.");
        if (manifest.Blobs == null)
            throw new InvalidDataException("The bundle manifest is missing its top-level blob index.");

        LibraryIdValidator.ValidateLibraryId(manifest.Library.Id);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach(BundleVersionEntry version in manifest.Versions)
        {
            if (!IsSingleVersionSegment(version.Version))
                throw new InvalidDataException("A bundle version identifier is not a single, non-empty path segment.");
            if (version.Blobs == null)
                throw new InvalidDataException($"Bundle version '{version.Version}' is missing its blob index.");
            if (!versions.Add(version.Version))
                throw new InvalidDataException($"The bundle contains duplicate version id '{version.Version}'.");
        }
    }

    private static void ValidateDirectoryOptions(BundleDirectoryInfo? directory)
    {
        if (directory != null)
        {
            if (directory.AllowedExtensions == null)
                throw new InvalidDataException("Directory package allowedExtensions cannot be null.");
            if (directory.ExclusionPatterns == null)
                throw new InvalidDataException("Directory package exclusionPatterns cannot be null.");
        }
    }

    private void ValidateDocumentRepositoryAvailability(ValidatedImportPackage package)
    {
        bool hasDocumentLifecycleData = package.Sources.Count > 0 ||
                                        package.Catalogs.Count > 0 ||
                                        package.Versions.Values.Any(version =>
                                            version.DocumentRevisions.Count > 0 ||
                                            version.SubjectAssignments.Count > 0 ||
                                            !string.IsNullOrWhiteSpace(
                                                version.VersionRecord.SubjectTaxonomyVersion));
        bool repositoriesAvailable = mSourceDocumentRepository != null &&
                                     mSubjectCatalogRepository != null &&
                                     mSubjectAssignmentRepository != null;
        if (hasDocumentLifecycleData && !repositoriesAvailable)
        {
            throw new InvalidOperationException(
                "This package contains document lifecycle or subject taxonomy data, but the importer was created without document repositories.");
        }
    }

    private static async Task<ValidatedImportPackage> MaterializeAndValidatePackageAsync(
        IBundleReader reader,
        BundleManifest manifest,
        CancellationToken ct)
    {
        if (!manifest.Blobs.ContainsKey(BundlePaths.LibraryFile))
            throw new InvalidDataException("The bundle manifest does not declare library.json.");

        LibraryRecord library = await ReadJsonAsync<LibraryRecord>(reader, BundlePaths.LibraryFile, ct);
        ValidateLibraryRecord(library, manifest);
        IReadOnlyDictionary<string, SourceDocumentRecord> sources =
            CreateSourceDocumentMap(await ReadTopLevelJsonlAsync<SourceDocumentRecord>(reader,
                                                                                       manifest,
                                                                                       BundlePaths.SourcesFile,
                                                                                       ct),
                                    manifest.Library.Id);
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> catalogs =
            CreateSubjectCatalogMap(await ReadTopLevelJsonlAsync<SubjectCatalogRecord>(reader,
                                                                                        manifest,
                                                                                        BundlePaths.SubjectCatalogsFile,
                                                                                        ct),
                                    manifest.Library.Id);
        var versions = new Dictionary<string, ValidatedVersionPackage>(StringComparer.Ordinal);
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var chunkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach(BundleVersionEntry versionEntry in manifest.Versions)
        {
            ValidatedVersionPackage version = await MaterializeVersionPackageAsync(reader,
                                                                                    versionEntry,
                                                                                    ct);
            await ValidateVersionPackageAsync(reader,
                                              manifest,
                                              versionEntry,
                                              version,
                                              sources,
                                              catalogs,
                                              pageIds,
                                              chunkIds,
                                              ct);
            versions.Add(versionEntry.Version, version);
        }

        return new ValidatedImportPackage(library, sources, catalogs, versions);
    }

    private static async Task<ValidatedVersionPackage> MaterializeVersionPackageAsync(
        IBundleReader reader,
        BundleVersionEntry versionEntry,
        CancellationToken ct)
    {
        string version = versionEntry.Version;
        IReadOnlyDictionary<string, BlobInfo> blobs = versionEntry.Blobs;
        string versionPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionFile);
        if (!blobs.ContainsKey(versionPath))
            throw new InvalidDataException($"Bundle version '{version}' does not declare version.json.");

        LibraryVersionRecord versionRecord = await ReadJsonAsync<LibraryVersionRecord>(reader, versionPath, ct);
        string profilePath = BundlePaths.VersionFilePath(version, BundlePaths.ProfileFile);
        LibraryProfile? profile = blobs.ContainsKey(profilePath)
                                      ? await ReadJsonAsync<LibraryProfile>(reader, profilePath, ct)
                                      : null;
        string indexPath = BundlePaths.VersionFilePath(version, BundlePaths.IndexFile);
        LibraryIndex? index = blobs.ContainsKey(indexPath)
                                  ? await ReadJsonAsync<LibraryIndex>(reader, indexPath, ct)
                                  : null;
        string diffPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionDiffFile);
        VersionDiffRecord? diff = blobs.ContainsKey(diffPath)
                                      ? await ReadJsonAsync<VersionDiffRecord>(reader, diffPath, ct)
                                      : null;
        IReadOnlyList<ExcludedSymbol> excluded = await ReadVersionJsonlAsync<ExcludedSymbol>(
                                                       reader,
                                                       blobs,
                                                       version,
                                                       BundlePaths.ExcludedSymbolsFile,
                                                       ct);
        IReadOnlyList<PageRecord> pages = await ReadVersionJsonlAsync<PageRecord>(reader,
                                                                                  blobs,
                                                                                  version,
                                                                                  BundlePaths.PagesFile,
                                                                                  ct);
        IReadOnlyList<DocChunk> chunks = await ReadVersionJsonlAsync<DocChunk>(reader,
                                                                              blobs,
                                                                              version,
                                                                              BundlePaths.ChunksFile,
                                                                              ct);
        IReadOnlyList<DocumentRevisionRecord> revisions =
            await ReadVersionJsonlAsync<DocumentRevisionRecord>(reader,
                                                                blobs,
                                                                version,
                                                                BundlePaths.DocumentRevisionsFile,
                                                                ct);
        IReadOnlyList<SubjectAssignmentRecord> assignments =
            await ReadVersionJsonlAsync<SubjectAssignmentRecord>(reader,
                                                                 blobs,
                                                                 version,
                                                                 BundlePaths.SubjectAssignmentsFile,
                                                                 ct);
        IReadOnlyList<Bm25Shard> shards = await ReadVersionJsonlAsync<Bm25Shard>(reader,
                                                                                blobs,
                                                                                version,
                                                                                BundlePaths.Bm25ShardsFile,
                                                                                ct);
        string gridFsPrefix = BundlePaths.VersionDir(version) + "/" + BundlePaths.Bm25GridFsDir + "/";
        IReadOnlyList<string> gridFsPaths = blobs.Keys
                                                  .Where(path => path.StartsWith(gridFsPrefix,
                                                                                 StringComparison.Ordinal))
                                                  .OrderBy(path => path, StringComparer.Ordinal)
                                                  .ToList();
        return new ValidatedVersionPackage(versionRecord,
                                           profile,
                                           index,
                                           diff,
                                           excluded,
                                           pages,
                                           chunks,
                                           revisions,
                                           assignments,
                                           shards,
                                           gridFsPaths);
    }

    private static Task<IReadOnlyList<T>> ReadVersionJsonlAsync<T>(
        IBundleReader reader,
        IReadOnlyDictionary<string, BlobInfo> blobs,
        string version,
        string fileName,
        CancellationToken ct)
    {
        string path = BundlePaths.VersionFilePath(version, fileName);
        return blobs.ContainsKey(path)
                   ? ReadJsonlAsync<T>(reader, path, ct)
                   : Task.FromResult<IReadOnlyList<T>>([]);
    }

    private static void ValidateLibraryRecord(LibraryRecord library, BundleManifest manifest)
    {
        if (!string.Equals(library.Id, manifest.Library.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The library record identity does not match the bundle manifest.");
        if (library.AllVersions == null ||
            manifest.Versions.Any(version => !library.AllVersions.Contains(version.Version,
                                                                             StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The library record does not contain every manifest version.");
        }
    }

    private static async Task ValidateVersionPackageAsync(
        IBundleReader reader,
        BundleManifest manifest,
        BundleVersionEntry versionEntry,
        ValidatedVersionPackage package,
        IReadOnlyDictionary<string, SourceDocumentRecord> sources,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> catalogs,
        ISet<string> pageIds,
        ISet<string> chunkIds,
        CancellationToken ct)
    {
        string libraryId = manifest.Library.Id;
        string version = versionEntry.Version;
        LibraryVersionRecord versionRecord = package.VersionRecord;
        ValidateScopedIdentity(LibraryVersionRecordType,
                               versionRecord.Id,
                               versionRecord.LibraryId,
                               versionRecord.Version,
                               libraryId,
                               version,
                               $"{libraryId}/{version}");
        if (versionRecord.PublicationState != VersionPublicationState.Published)
            throw new InvalidDataException("A package can import only published library versions.");
        if (versionEntry.EmbeddingDimensions < 1 ||
            versionRecord.EmbeddingDimensions != versionEntry.EmbeddingDimensions ||
            !string.Equals(versionRecord.EmbeddingProviderId,
                           versionEntry.EmbeddingProviderId,
                           StringComparison.Ordinal) ||
            !string.Equals(versionRecord.EmbeddingModelName,
                           versionEntry.EmbeddingModelName,
                           StringComparison.Ordinal))
        {
            throw new InvalidDataException("Library-version encoder metadata does not match the manifest.");
        }
        if (versionEntry.PageCount != package.Pages.Count ||
            versionRecord.PageCount != package.Pages.Count ||
            versionEntry.ChunkCount != package.Chunks.Count ||
            versionRecord.ChunkCount != package.Chunks.Count)
        {
            throw new InvalidDataException("Library-version page or chunk counts do not match the package rows.");
        }

        ValidateOptionalVersionMetadata(package, libraryId, version, versionRecord);
        IReadOnlyDictionary<string, DocumentRevisionRecord> revisions =
            ValidateDocumentRecords(manifest, versionEntry, package, sources, catalogs);
        ValidateExcludedSymbols(package.ExcludedSymbols, libraryId, version);
        ValidatePages(package.Pages, libraryId, version, revisions, pageIds);
        ValidateChunks(package.Chunks, libraryId, version, revisions, chunkIds);
        ValidateEmbeddingBlob(versionEntry, package.Chunks.Count);
        await ValidateBm25Async(reader, versionEntry, package, ct);
    }

    private static void ValidateOptionalVersionMetadata(ValidatedVersionPackage package,
                                                        string libraryId,
                                                        string version,
                                                        LibraryVersionRecord versionRecord)
    {
        string canonicalId = $"{libraryId}/{version}";
        if (package.Profile != null)
            ValidateScopedIdentity(LibraryProfileRecordType,
                                   package.Profile.Id,
                                   package.Profile.LibraryId,
                                   package.Profile.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
        if (package.Index != null)
            ValidateScopedIdentity(LibraryIndexRecordType,
                                   package.Index.Id,
                                   package.Index.LibraryId,
                                   package.Index.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
        if (package.Diff != null)
        {
            VersionDiffRecord diff = package.Diff;
            if (!string.Equals(diff.LibraryId, libraryId, StringComparison.Ordinal) ||
                !IsSingleVersionSegment(diff.FromVersion) ||
                !string.Equals(diff.ToVersion, version, StringComparison.Ordinal) ||
                !string.Equals(diff.Id,
                               $"{libraryId}/{diff.FromVersion}-to-{diff.ToVersion}",
                               StringComparison.Ordinal) ||
                !string.Equals(versionRecord.PreviousVersion, diff.FromVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The version-diff identity does not match its manifest version.");
            }
        }
    }

    private static IReadOnlyDictionary<string, DocumentRevisionRecord> ValidateDocumentRecords(
        BundleManifest manifest,
        BundleVersionEntry versionEntry,
        ValidatedVersionPackage package,
        IReadOnlyDictionary<string, SourceDocumentRecord> sources,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> catalogs)
    {
        string libraryId = manifest.Library.Id;
        string version = versionEntry.Version;
        SubjectCatalogRecord? versionCatalog = ValidateVersionSubjectCatalog(package.VersionRecord,
                                                                               package.SubjectAssignments.Count,
                                                                               catalogs);
        var revisions = new Dictionary<string, DocumentRevisionRecord>(StringComparer.Ordinal);
        foreach(DocumentRevisionRecord revision in package.DocumentRevisions)
        {
            if (string.IsNullOrWhiteSpace(revision.DocumentId))
                throw new InvalidDataException("A document revision is missing its document identity.");
            string canonicalId = SourceDocumentRepository.MakeRevisionId(libraryId,
                                                                          version,
                                                                          revision.DocumentId);
            ValidateScopedIdentity(DocumentRevisionRecordType,
                                   revision.Id,
                                   revision.LibraryId,
                                   revision.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
            if (revision.State != DocumentRevisionState.Published)
                throw new InvalidDataException("A package can import only published document revisions.");
            if (!sources.ContainsKey(revision.DocumentId))
                throw new InvalidDataException($"Missing source document '{revision.DocumentId}'.");
            if (!revisions.TryAdd(revision.Id, revision))
                throw new InvalidDataException($"The bundle contains duplicate document-revision id '{revision.Id}'.");
            ValidateDocumentArtifact(manifest,
                                     revision.Id,
                                     revision.OriginalArtifactHash,
                                     revision.OriginalByteLength);
            if (string.IsNullOrWhiteSpace(revision.ExtractionArtifactHash) &&
                revision.ExtractionByteLength != null)
            {
                throw new InvalidDataException($"Revision '{revision.Id}' has an extraction length without a hash.");
            }
            if (!string.IsNullOrWhiteSpace(revision.ExtractionArtifactHash))
            {
                if (revision.ExtractionByteLength == null)
                    throw new InvalidDataException($"Revision '{revision.Id}' has no extraction byte length.");
                ValidateDocumentArtifact(manifest,
                                         revision.Id,
                                         revision.ExtractionArtifactHash,
                                         revision.ExtractionByteLength.Value);
            }
        }

        var assignmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach(SubjectAssignmentRecord assignment in package.SubjectAssignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.DocumentRevisionId) ||
                string.IsNullOrWhiteSpace(assignment.DocumentId) ||
                string.IsNullOrWhiteSpace(assignment.TaxonomyVersion))
            {
                throw new InvalidDataException("A subject assignment is missing required identity metadata.");
            }
            string canonicalId = SubjectAssignmentRepository.MakeId(libraryId,
                                                                     version,
                                                                     assignment.DocumentRevisionId);
            ValidateScopedIdentity(SubjectAssignmentRecordType,
                                   assignment.Id,
                                   assignment.LibraryId,
                                   assignment.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
            if (!assignmentIds.Add(assignment.Id))
                throw new InvalidDataException($"The bundle contains duplicate subject-assignment id '{assignment.Id}'.");
            if (!revisions.TryGetValue(assignment.DocumentRevisionId, out DocumentRevisionRecord? revision) ||
                !string.Equals(revision.DocumentId, assignment.DocumentId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Subject assignment '{assignment.Id}' does not reference its package revision.");
            }
            if (!string.Equals(assignment.TaxonomyVersion,
                               package.VersionRecord.SubjectTaxonomyVersion,
                               StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Subject assignment '{assignment.Id}' taxonomy does not match its library version.");
            }
            SubjectCatalogRecord assignmentCatalog = versionCatalog ??
                                                     throw new InvalidDataException(
                                                         "A library version with subject assignments is missing its taxonomy.");
            ValidateSubjectAssignmentMembership(assignment, assignmentCatalog);
        }

        if (manifest.ManifestVersion >= 2 &&
            (versionEntry.SourceDocumentCount != package.DocumentRevisions
                                                        .Select(revision => revision.DocumentId)
                                                        .Distinct(StringComparer.Ordinal)
                                                        .Count() ||
             versionEntry.DocumentRevisionCount != package.DocumentRevisions.Count ||
             versionEntry.SubjectAssignmentCount != package.SubjectAssignments.Count))
        {
            throw new InvalidDataException("Document metadata counts do not match the package rows.");
        }

        return revisions;
    }

    private static SubjectCatalogRecord? ValidateVersionSubjectCatalog(
        LibraryVersionRecord versionRecord,
        int assignmentCount,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> catalogs)
    {
        SubjectCatalogRecord? result = null;
        string? taxonomyVersion = versionRecord.SubjectTaxonomyVersion;
        if (string.IsNullOrWhiteSpace(taxonomyVersion))
        {
            if (assignmentCount > 0)
                throw new InvalidDataException("A library version with subject assignments is missing its taxonomy.");
        }
        else
        {
            var catalogKey = new SubjectCatalogKey(versionRecord.LibraryId, taxonomyVersion);
            if (!catalogs.TryGetValue(catalogKey, out result))
                throw new InvalidDataException($"Missing subject catalog '{taxonomyVersion}'.");
            if (string.IsNullOrWhiteSpace(result.ScanRunId))
                throw new InvalidDataException("Manifest-v2 subject catalogs require scan ownership.");
        }

        return result;
    }

    private static void ValidateDocumentArtifact(BundleManifest manifest,
                                                 string revisionId,
                                                 string hash,
                                                 long byteLength)
    {
        if (string.IsNullOrWhiteSpace(hash) ||
            hash.Length != 64 ||
            hash.Any(character => !Uri.IsHexDigit(character)) ||
            byteLength < 0)
        {
            throw new InvalidDataException($"Revision '{revisionId}' has invalid artifact metadata.");
        }
        string path = BundlePaths.DocumentArtifact(hash);
        if (!manifest.Blobs.TryGetValue(path, out BlobInfo? blob) || blob.Bytes != byteLength)
            throw new InvalidDataException($"Revision '{revisionId}' references a missing artifact.");
    }

    private static void ValidateSubjectAssignmentMembership(SubjectAssignmentRecord assignment,
                                                            SubjectCatalogRecord catalog)
    {
        var subjectIds = catalog.Concepts.Select(concept => concept.Id)
                                .ToHashSet(StringComparer.Ordinal);
        ValidateSubjectSelectionMembership(assignment, assignment.Primary, subjectIds, PrimarySubjectRole);
        foreach(SubjectSelection secondary in assignment.Secondary)
            ValidateSubjectSelectionMembership(assignment, secondary, subjectIds, SecondarySubjectRole);
    }

    private static void ValidateSubjectSelectionMembership(SubjectAssignmentRecord assignment,
                                                           SubjectSelection selection,
                                                           IReadOnlySet<string> subjectIds,
                                                           string role)
    {
        if (selection == null ||
            string.IsNullOrWhiteSpace(selection.SubjectId) ||
            !subjectIds.Contains(selection.SubjectId))
        {
            throw new InvalidDataException(
                $"Subject assignment '{assignment.Id}' has a {role} subject outside catalog '{assignment.TaxonomyVersion}'.");
        }
    }

    private static void ValidateExcludedSymbols(IReadOnlyList<ExcludedSymbol> symbols,
                                                string libraryId,
                                                string version)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach(ExcludedSymbol symbol in symbols)
        {
            string canonicalId = string.IsNullOrWhiteSpace(symbol.Name)
                                     ? string.Empty
                                     : ExcludedSymbol.MakeId(libraryId, version, symbol.Name);
            ValidateScopedIdentity(ExcludedSymbolRecordType,
                                   symbol.Id,
                                   symbol.LibraryId,
                                   symbol.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
            if (!ids.Add(symbol.Id))
                throw new InvalidDataException($"The bundle contains duplicate excluded-symbol id '{symbol.Id}'.");
        }
    }

    private static void ValidatePages(IReadOnlyList<PageRecord> pages,
                                      string libraryId,
                                      string version,
                                      IReadOnlyDictionary<string, DocumentRevisionRecord> revisions,
                                      ISet<string> pageIds)
    {
        foreach(PageRecord page in pages)
        {
            ValidateDocumentProvenance(PageRecordType, page.Id, page.DocumentSource, revisions);
            string? canonicalId = page.DocumentSource == null
                                      ? null
                                      : CreateDocumentPageId(page, libraryId, version);
            ValidateScopedIdentity(PageRecordType,
                                   page.Id,
                                   page.LibraryId,
                                   page.Version,
                                   libraryId,
                                   version,
                                   canonicalId);
            if (canonicalId == null && !HasScopedIdNamespace(page.Id, libraryId, version))
                throw new InvalidDataException("The page identity is outside its manifest namespace.");
            if (!pageIds.Add(page.Id))
                throw new InvalidDataException($"The bundle contains duplicate page id '{page.Id}'.");
        }
    }

    private static void ValidateChunks(IReadOnlyList<DocChunk> chunks,
                                       string libraryId,
                                       string version,
                                       IReadOnlyDictionary<string, DocumentRevisionRecord> revisions,
                                       ISet<string> chunkIds)
    {
        foreach(DocChunk chunk in chunks)
        {
            ValidateScopedIdentity(ChunkRecordType,
                                   chunk.Id,
                                   chunk.LibraryId,
                                   chunk.Version,
                                   libraryId,
                                   version);
            if (!HasScopedIdNamespace(chunk.Id, libraryId, version))
                throw new InvalidDataException("The chunk identity is outside its manifest namespace.");
            if (!chunkIds.Add(chunk.Id))
                throw new InvalidDataException($"The bundle contains duplicate chunk id '{chunk.Id}'.");
            ValidateDocumentProvenance(ChunkRecordType, chunk.Id, chunk.DocumentSource, revisions);
        }
    }

    private static string CreateDocumentPageId(PageRecord page, string libraryId, string version)
    {
        DocumentProvenance provenance = page.DocumentSource ?? throw new InvalidDataException(
            "A document page is missing provenance.");
        if (string.IsNullOrWhiteSpace(page.Url) ||
            string.IsNullOrWhiteSpace(provenance.DocumentId) ||
            !TryReadSectionOrder(page.Url, out int sectionOrder))
        {
            throw new InvalidDataException($"Document page '{page.Id}' has malformed section identity metadata.");
        }

        string identity = string.Join(IdentitySeparator,
                                      libraryId,
                                      version,
                                      provenance.DocumentId,
                                      sectionOrder.ToString(CultureInfo.InvariantCulture));
        return DocumentPageIdPrefix + HashIdentity(identity);
    }

    private static bool TryReadSectionOrder(string url, out int sectionOrder)
    {
        sectionOrder = 0;
        int marker = url.LastIndexOf(PageSectionMarker, StringComparison.Ordinal);
        bool result = marker >= 0 &&
                      int.TryParse(url.AsSpan(marker + PageSectionMarker.Length),
                                   NumberStyles.None,
                                   CultureInfo.InvariantCulture,
                                   out sectionOrder) &&
                      sectionOrder >= 0;
        return result;
    }

    private static bool HasScopedIdNamespace(string id, string libraryId, string version)
    {
        string prefix = $"{libraryId}/{version}/";
        return id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length;
    }

    private static string HashIdentity(string identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private static void ValidateDocumentProvenance(
        string recordType,
        string recordId,
        DocumentProvenance? provenance,
        IReadOnlyDictionary<string, DocumentRevisionRecord> revisions)
    {
        if (provenance != null &&
            (string.IsNullOrWhiteSpace(provenance.RevisionId) ||
             string.IsNullOrWhiteSpace(provenance.DocumentId) ||
             !revisions.TryGetValue(provenance.RevisionId, out DocumentRevisionRecord? revision) ||
             !string.Equals(revision.DocumentId, provenance.DocumentId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The {recordType} '{recordId}' does not reference its package document revision.");
        }
    }

    private static void ValidateEmbeddingBlob(BundleVersionEntry versionEntry, int chunkCount)
    {
        string chunksPath = BundlePaths.VersionFilePath(versionEntry.Version, BundlePaths.ChunksFile);
        string embeddingsPath = BundlePaths.VersionFilePath(versionEntry.Version, BundlePaths.EmbeddingsBlobFile);
        bool hasChunks = versionEntry.Blobs.ContainsKey(chunksPath);
        bool hasEmbeddings = versionEntry.Blobs.TryGetValue(embeddingsPath, out BlobInfo? embeddings);
        if (hasChunks != hasEmbeddings)
            throw new InvalidDataException("Chunk rows and their embedding blob must be packaged together.");
        if (hasEmbeddings)
        {
            if (embeddings == null)
                throw new InvalidDataException("The package contains null embedding metadata.");
            long expectedBytes = checked((long)chunkCount * versionEntry.EmbeddingDimensions * BytesPerFloat);
            if (embeddings.Bytes != expectedBytes)
                throw new InvalidDataException("The embedding blob length does not match the package chunk rows.");
        }
    }

    private static async Task ValidateBm25Async(IBundleReader reader,
                                                BundleVersionEntry versionEntry,
                                                ValidatedVersionPackage package,
                                                CancellationToken ct)
    {
        var availableGridFsPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(string path in package.Bm25GridFsPaths)
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(id) || !availableGridFsPaths.TryAdd(id, path))
                throw new InvalidDataException("The package contains duplicate or malformed BM25 GridFS identities.");
        }

        if (package.Index == null)
        {
            if (package.Bm25Shards.Count > 0 || availableGridFsPaths.Count > 0 || versionEntry.Bm25HasGridFs)
                throw new InvalidDataException("BM25 package records require a library index.");
        }
        else
            await ValidateBm25IndexAsync(reader, versionEntry, package, availableGridFsPaths, ct);
    }

    private static async Task ValidateBm25IndexAsync(
        IBundleReader reader,
        BundleVersionEntry versionEntry,
        ValidatedVersionPackage package,
        IReadOnlyDictionary<string, string> availableGridFsPaths,
        CancellationToken ct)
    {
        Bm25Stats stats = package.Index?.Bm25 ??
                          throw new InvalidDataException("The library index is missing its BM25 statistics.");
        IReadOnlyDictionary<string, int> docLengths = stats.DocLengths ??
                                                       throw new InvalidDataException(
                                                           "The BM25 statistics are missing document lengths.");
        var packageChunkIds = package.Chunks.Select(chunk => chunk.Id)
                                     .ToHashSet(StringComparer.Ordinal);
        ValidateBm25Stats(stats, docLengths, packageChunkIds);

        if (stats.ShardCount == 0)
        {
            if (docLengths.Count > 0 ||
                stats.DocumentCount != 0 ||
                stats.AverageDocLength != 0 ||
                package.Bm25Shards.Count > 0 ||
                availableGridFsPaths.Count > 0 ||
                versionEntry.Bm25HasGridFs)
            {
                throw new InvalidDataException("The package contains an incomplete legacy BM25 index.");
            }
        }
        else
        {
            await ValidateBm25ShardsAsync(reader,
                                          versionEntry,
                                          package,
                                          stats,
                                          docLengths,
                                          packageChunkIds,
                                          availableGridFsPaths,
                                          ct);
        }
    }

    private static async Task ValidateBm25ShardsAsync(
        IBundleReader reader,
        BundleVersionEntry versionEntry,
        ValidatedVersionPackage package,
        Bm25Stats stats,
        IReadOnlyDictionary<string, int> docLengths,
        IReadOnlySet<string> packageChunkIds,
        IReadOnlyDictionary<string, string> availableGridFsPaths,
        CancellationToken ct)
    {
        string libraryId = package.VersionRecord.LibraryId;
        string version = versionEntry.Version;
        var referencedGridFsIds = new HashSet<string>(StringComparer.Ordinal);
        var shardIds = new HashSet<string>(StringComparer.Ordinal);
        var shardIndexes = new HashSet<int>();
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var postingFrequencyTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach(Bm25Shard shard in package.Bm25Shards)
            await ValidateBm25ShardAsync(reader,
                                         shard,
                                         libraryId,
                                         version,
                                         stats.ShardCount,
                                         packageChunkIds,
                                         shardIds,
                                         shardIndexes,
                                         terms,
                                         postingFrequencyTotals,
                                         referencedGridFsIds,
                                         availableGridFsPaths,
                                         ct);

        if (!referencedGridFsIds.SetEquals(availableGridFsPaths.Keys))
            throw new InvalidDataException("BM25 GridFS references do not match the package blobs.");
        if (versionEntry.Bm25HasGridFs != (referencedGridFsIds.Count > 0))
            throw new InvalidDataException("BM25 GridFS metadata does not match the package records.");

        ValidateBm25FrequencyTotals(docLengths, postingFrequencyTotals);
    }

    private static async Task ValidateBm25ShardAsync(
        IBundleReader reader,
        Bm25Shard shard,
        string libraryId,
        string version,
        int shardCount,
        IReadOnlySet<string> packageChunkIds,
        ISet<string> shardIds,
        ISet<int> shardIndexes,
        ISet<string> terms,
        IDictionary<string, long> postingFrequencyTotals,
        ISet<string> referencedGridFsIds,
        IReadOnlyDictionary<string, string> availableGridFsPaths,
        CancellationToken ct)
    {
        if (shard.ExternalTerms == null || shard.InlineTerms == null)
            throw new InvalidDataException("A BM25 shard is missing its term collections.");
        string canonicalId = $"{libraryId}/{version}/{shard.ShardIndex}";
        ValidateScopedIdentity(Bm25ShardRecordType,
                               shard.Id,
                               shard.LibraryId,
                               shard.Version,
                               libraryId,
                               version,
                               canonicalId);
        if (shard.ShardIndex < 0 ||
            shard.ShardIndex >= shardCount ||
            !shardIds.Add(shard.Id) ||
            !shardIndexes.Add(shard.ShardIndex))
        {
            throw new InvalidDataException($"The package contains a malformed BM25 shard '{shard.Id}'.");
        }

        if (shard.ShardGridFsRef != null)
        {
            await ValidateWholeBm25ShardAsync(reader,
                                              shard,
                                              shardCount,
                                              packageChunkIds,
                                              terms,
                                              postingFrequencyTotals,
                                              referencedGridFsIds,
                                              availableGridFsPaths,
                                              ct);
        }
        else
        {
            foreach((string term, IReadOnlyList<Bm25Posting> postings) in shard.InlineTerms)
                ValidateBm25Term(term,
                                 postings,
                                 shard,
                                 shardCount,
                                 packageChunkIds,
                                 terms,
                                 postingFrequencyTotals);

            foreach((string term, string gridFsId) in shard.ExternalTerms)
            {
                string payloadPath = AddBm25GridFsReference(shard.Id,
                                                            gridFsId,
                                                            referencedGridFsIds,
                                                            availableGridFsPaths);
                using JsonDocument payload = await ReadCompressedBm25PayloadAsync(reader, payloadPath, ct);
                IReadOnlyList<Bm25Posting> postings = DeserializeBm25Postings(payload.RootElement,
                                                                              payloadPath);
                ValidateBm25Term(term,
                                 postings,
                                 shard,
                                 shardCount,
                                 packageChunkIds,
                                 terms,
                                 postingFrequencyTotals);
            }

            if (shard.InlineTerms.Count == 0 && shard.ExternalTerms.Count == 0)
                throw new InvalidDataException($"BM25 shard '{shard.Id}' does not contain any terms.");
        }
    }

    private static async Task ValidateWholeBm25ShardAsync(
        IBundleReader reader,
        Bm25Shard shard,
        int shardCount,
        IReadOnlySet<string> packageChunkIds,
        ISet<string> terms,
        IDictionary<string, long> postingFrequencyTotals,
        ISet<string> referencedGridFsIds,
        IReadOnlyDictionary<string, string> availableGridFsPaths,
        CancellationToken ct)
    {
        string gridFsId = shard.ShardGridFsRef ?? string.Empty;
        if (string.IsNullOrWhiteSpace(gridFsId) ||
            shard.InlineTerms.Count > 0 ||
            shard.ExternalTerms.Count > 0)
        {
            throw new InvalidDataException($"BM25 shard '{shard.Id}' has an invalid whole-shard spill.");
        }

        string payloadPath = AddBm25GridFsReference(shard.Id,
                                                    gridFsId,
                                                    referencedGridFsIds,
                                                    availableGridFsPaths);
        using JsonDocument payload = await ReadCompressedBm25PayloadAsync(reader, payloadPath, ct);
        ValidateWholeShardPayload(payload.RootElement,
                                  payloadPath,
                                  shard,
                                  shardCount,
                                  packageChunkIds,
                                  terms,
                                  postingFrequencyTotals);
    }

    private static void ValidateBm25Stats(Bm25Stats stats,
                                          IReadOnlyDictionary<string, int> docLengths,
                                          IReadOnlySet<string> packageChunkIds)
    {
        if (stats.ShardCount < 0 || stats.DocumentCount != docLengths.Count)
            throw new InvalidDataException("The BM25 statistics contain invalid counts.");

        foreach((string chunkId, int length) in docLengths)
        {
            if (string.IsNullOrWhiteSpace(chunkId) || length < 1 || !packageChunkIds.Contains(chunkId))
                throw new InvalidDataException("The BM25 document lengths do not match the package chunks.");
        }

        double expectedAverage = docLengths.Count == 0 ? 0 : docLengths.Values.Average();
        double tolerance = Math.Max(1, Math.Abs(expectedAverage)) * Bm25AverageTolerance;
        if (!double.IsFinite(stats.AverageDocLength) ||
            stats.AverageDocLength < 0 ||
            Math.Abs(stats.AverageDocLength - expectedAverage) > tolerance)
        {
            throw new InvalidDataException("The BM25 average document length does not match its document lengths.");
        }
    }

    private static string AddBm25GridFsReference(
        string shardId,
        string gridFsId,
        ISet<string> referencedGridFsIds,
        IReadOnlyDictionary<string, string> availableGridFsPaths)
    {
        if (string.IsNullOrWhiteSpace(gridFsId) ||
            !referencedGridFsIds.Add(gridFsId) ||
            !availableGridFsPaths.TryGetValue(gridFsId, out string? path))
        {
            throw new InvalidDataException($"BM25 shard '{shardId}' has a malformed GridFS reference.");
        }

        return path;
    }

    private static async Task<JsonDocument> ReadCompressedBm25PayloadAsync(IBundleReader reader,
                                                                            string path,
                                                                            CancellationToken ct)
    {
        JsonDocument result;
        try
        {
            await using Stream stream = reader.OpenEntry(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            result = await JsonDocument.ParseAsync(gzip, cancellationToken: ct);
        }
        catch(Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InvalidDataException($"BM25 GridFS payload '{path}' is invalid.", exception);
        }
        return result;
    }

    private static void ValidateWholeShardPayload(JsonElement payload,
                                                  string payloadId,
                                                  Bm25Shard shard,
                                                  int shardCount,
                                                  IReadOnlySet<string> packageChunkIds,
                                                  ISet<string> terms,
                                                  IDictionary<string, long> postingFrequencyTotals)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"BM25 shard '{shard.Id}' has an invalid whole-shard payload.");

        var payloadTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach(JsonProperty property in payload.EnumerateObject())
        {
            if (!payloadTerms.Add(property.Name))
                throw new InvalidDataException($"BM25 shard '{shard.Id}' contains a duplicate term.");
            IReadOnlyList<Bm25Posting> postings = DeserializeBm25Postings(property.Value,
                                                                          payloadId);
            ValidateBm25Term(property.Name,
                             postings,
                             shard,
                             shardCount,
                             packageChunkIds,
                             terms,
                             postingFrequencyTotals);
        }

        if (payloadTerms.Count == 0)
            throw new InvalidDataException($"BM25 shard '{shard.Id}' does not contain any terms.");
    }

    private static IReadOnlyList<Bm25Posting> DeserializeBm25Postings(JsonElement payload, string payloadId)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"BM25 GridFS payload '{payloadId}' is not a postings list.");

        IReadOnlyList<Bm25Posting> result;
        try
        {
            result = payload.Deserialize<List<Bm25Posting>>(BundleJsonOptions.Default) ??
                     throw new InvalidDataException($"BM25 GridFS payload '{payloadId}' is empty.");
        }
        catch(JsonException exception)
        {
            throw new InvalidDataException($"BM25 GridFS payload '{payloadId}' is invalid.", exception);
        }
        return result;
    }

    private static void ValidateBm25Term(string term,
                                         IReadOnlyList<Bm25Posting>? postings,
                                         Bm25Shard shard,
                                         int shardCount,
                                         IReadOnlySet<string> packageChunkIds,
                                         ISet<string> terms,
                                         IDictionary<string, long> postingFrequencyTotals)
    {
        if (string.IsNullOrWhiteSpace(term) ||
            !terms.Add(term) ||
            Bm25ShardIndexFor(term, shardCount) != shard.ShardIndex ||
            postings == null ||
            postings.Count == 0)
        {
            throw new InvalidDataException($"BM25 shard '{shard.Id}' contains a malformed term.");
        }

        var postingChunkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach(Bm25Posting posting in postings)
        {
            if (posting == null ||
                string.IsNullOrWhiteSpace(posting.ChunkId) ||
                posting.TermFrequency < 1 ||
                !packageChunkIds.Contains(posting.ChunkId) ||
                !postingChunkIds.Add(posting.ChunkId))
            {
                throw new InvalidDataException($"BM25 term '{term}' contains a malformed posting.");
            }

            postingFrequencyTotals.TryGetValue(posting.ChunkId, out long current);
            try
            {
                postingFrequencyTotals[posting.ChunkId] = checked(current + posting.TermFrequency);
            }
            catch(OverflowException exception)
            {
                throw new InvalidDataException($"BM25 term '{term}' has an invalid frequency total.", exception);
            }
        }
    }

    private static void ValidateBm25FrequencyTotals(IReadOnlyDictionary<string, int> docLengths,
                                                    IReadOnlyDictionary<string, long> postingFrequencyTotals)
    {
        if (docLengths.Count != postingFrequencyTotals.Count ||
            docLengths.Any(pair => !postingFrequencyTotals.TryGetValue(pair.Key, out long total) ||
                                     total != pair.Value))
        {
            throw new InvalidDataException("BM25 postings do not match the recorded document lengths.");
        }
    }

    private static int Bm25ShardIndexFor(string term, int shardCount)
    {
        unchecked
        {
            uint hash = Bm25HashSeed;
            foreach(char character in term)
                hash = hash * Bm25HashMultiplier ^ character;
            return (int)(hash % (uint)shardCount);
        }
    }

    private static void ValidateScopedIdentity(string recordType,
                                               string id,
                                               string actualLibraryId,
                                               string actualVersion,
                                               string expectedLibraryId,
                                               string expectedVersion,
                                               string? canonicalId = null)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !string.Equals(actualLibraryId, expectedLibraryId, StringComparison.Ordinal) ||
            !string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal) ||
            canonicalId != null && !string.Equals(id, canonicalId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {recordType} identity does not match its manifest library and version.");
        }
    }

    private static bool IsSingleVersionSegment(string version) =>
        !string.IsNullOrWhiteSpace(version) &&
        version is not "." and not ".." &&
        !version.Contains('/') &&
        !version.Contains('\\');

    private static IReadOnlyDictionary<string, SourceDocumentRecord> CreateSourceDocumentMap(
        IReadOnlyList<SourceDocumentRecord> sources,
        string libraryId)
    {
        var result = new Dictionary<string, SourceDocumentRecord>(StringComparer.Ordinal);
        foreach(SourceDocumentRecord source in sources)
        {
            string canonicalId = string.IsNullOrWhiteSpace(source.NormalizedRelativePath)
                                     ? string.Empty
                                     : SourceDocumentIdPrefix + HashIdentity(
                                           string.Join(IdentitySeparator,
                                                       libraryId,
                                                       source.NormalizedRelativePath));
            if (!string.Equals(source.Id, canonicalId, StringComparison.Ordinal) ||
                !string.Equals(source.LibraryId, libraryId, StringComparison.Ordinal))
                throw new InvalidDataException("A source-document identity does not match the bundle library.");
            if (!result.TryAdd(source.Id, source))
                throw new InvalidDataException($"The bundle contains duplicate source-document id '{source.Id}'.");
        }
        return result;
    }

    private static IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> CreateSubjectCatalogMap(
        IReadOnlyList<SubjectCatalogRecord> catalogs,
        string libraryId)
    {
        var result = new Dictionary<SubjectCatalogKey, SubjectCatalogRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach(SubjectCatalogRecord catalog in catalogs)
        {
            string canonicalId = string.IsNullOrWhiteSpace(catalog.TaxonomyVersion)
                                     ? string.Empty
                                     : SubjectCatalogRepository.MakeId(libraryId, catalog.TaxonomyVersion);
            if (!string.Equals(catalog.Id, canonicalId, StringComparison.Ordinal) ||
                !string.Equals(catalog.LibraryId, libraryId, StringComparison.Ordinal))
                throw new InvalidDataException("A subject-catalog identity does not match the bundle library.");
            if (catalog.PublicationState != SubjectCatalogPublicationState.Published)
            {
                throw new InvalidDataException(
                    $"Subject catalog '{catalog.TaxonomyVersion}' is not published.");
            }
            ValidateSubjectCatalogConcepts(catalog);
            var key = new SubjectCatalogKey(catalog.LibraryId, catalog.TaxonomyVersion);
            if (!ids.Add(catalog.Id) || !result.TryAdd(key, catalog))
                throw new InvalidDataException($"The bundle contains duplicate subject-catalog id '{catalog.Id}'.");
        }
        return result;
    }

    private static void ValidateSubjectCatalogConcepts(SubjectCatalogRecord catalog)
    {
        if (catalog.Concepts is not { Count: > 0 })
            throw new InvalidDataException($"Subject catalog '{catalog.TaxonomyVersion}' contains no subjects.");

        var conceptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach(SubjectConcept? concept in catalog.Concepts)
        {
            if (concept == null)
                throw new InvalidDataException(
                    $"Subject catalog '{catalog.TaxonomyVersion}' contains a null subject concept.");
            if (string.IsNullOrWhiteSpace(concept.Id) || !conceptIds.Add(concept.Id))
            {
                throw new InvalidDataException(
                    $"Subject catalog '{catalog.TaxonomyVersion}' contains a missing or duplicate subject id.");
            }
            if (string.IsNullOrWhiteSpace(concept.Label) || string.IsNullOrWhiteSpace(concept.Description))
            {
                throw new InvalidDataException(
                    $"Subject catalog '{catalog.TaxonomyVersion}' contains a subject without a label or description.");
            }
            if (concept.Aliases == null)
            {
                throw new InvalidDataException(
                    $"Subject catalog '{catalog.TaxonomyVersion}' contains a subject with a null alias list.");
            }
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string? alias in concept.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || !aliases.Add(alias))
                {
                    throw new InvalidDataException(
                        $"Subject catalog '{catalog.TaxonomyVersion}' contains a blank or duplicate subject alias.");
                }
            }
        }
    }

    private static DirectoryLibraryDefinition CreatePackageDefinition(BundleManifest manifest,
                                                                       LibraryVersionRecord publishedVersion) =>
        new()
            {
                Id = manifest.Library.Id,
                RootPath = string.Empty,
                Name = manifest.Library.Name,
                Hint = manifest.Library.Hint,
                Recursive = manifest.Directory?.Recursive ?? false,
                AllowedExtensions = NormalizeDirectoryExtensions(manifest.Directory?.AllowedExtensions),
                ExclusionPatterns = NormalizeDirectoryExclusions(manifest.Directory?.ExclusionPatterns),
                BindingStatus = DirectoryLibraryBindingStatus.Unbound,
                RegisteredAtUtc = DateTime.SpecifyKind(manifest.CreatedUtc, DateTimeKind.Utc),
                LastPublishedAtUtc = publishedVersion.ScrapedAt,
                LastPublishedVersion = publishedVersion.Version
            };

    private static IReadOnlyList<string> NormalizeDirectoryExtensions(IReadOnlyList<string>? extensions) =>
        (extensions ?? [])
        .Where(extension => !string.IsNullOrWhiteSpace(extension))
        .Select(extension => extension.Trim())
        .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
        .Select(extension => extension.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static IReadOnlyList<string> NormalizeDirectoryExclusions(IReadOnlyList<string>? exclusions) =>
        (exclusions ?? [])
        .Where(exclusion => !string.IsNullOrWhiteSpace(exclusion))
        .Select(exclusion => exclusion.Trim())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static void ValidateAllBlobs(IBundleReader reader, BundleManifest manifest, CancellationToken ct)
    {
        foreach (var versionEntry in manifest.Versions)
            ValidateBlobs(reader, versionEntry.Blobs, ct);

        ValidateBlobs(reader, manifest.Blobs, ct);
    }

    private static void ValidateBlobs(IBundleReader reader,
                                      IReadOnlyDictionary<string, BlobInfo> blobs,
                                      CancellationToken ct)
    {
        foreach (var (path, info) in blobs)
            ValidateBlob(reader, path, info, ct);
    }

    private static void ValidateBlob(IBundleReader reader, string path, BlobInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!reader.HasEntry(path))
            throw new InvalidOperationException($"Bundle manifest references missing entry '{path}'");

        using var stream = reader.OpenEntry(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read = stream.Read(buffer, 0, buffer.Length);
        while (read > 0)
        {
            hasher.AppendData(buffer, 0, read);
            bytes += read;
            read = stream.Read(buffer, 0, buffer.Length);
        }

        var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, info.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Bundle integrity check failed for '{path}': expected {info.Sha256}, got {actual}");
        if (bytes != info.Bytes)
            throw new InvalidOperationException(
                $"Bundle integrity check failed for '{path}': expected {info.Bytes} bytes, got {bytes}");
    }

    private async Task PersistReembedJobAsync(AttemptedReembedJob attempted,
                                               ImportModeScope modeScope,
                                               CancellationToken ct)
    {
        await RequireActiveModeLeaseAsync(modeScope.Lease, ct);
        try
        {
            await mJobRepository.UpsertAsync(attempted.Record, ct);
        }
        catch(Exception writeFailure)
        {
            JobRecord? current;
            try
            {
                current = await mJobRepository.GetAsync(attempted.Record.Id, CancellationToken.None);
            }
            catch(Exception confirmationFailure)
            {
                modeScope.MarkPublicationOutcomeUnknown();
                throw new ImportPublicationOutcomeUnknownException(
                    "Re-embed job publication failed and its durable outcome could not be confirmed.",
                    new AggregateException(writeFailure, confirmationFailure));
            }

            if (!ReembedJobsEquivalent(current, attempted.Record))
            {
                if (current == null)
                    ExceptionDispatchInfo.Capture(writeFailure).Throw();

                modeScope.MarkPublicationOutcomeUnknown();
                throw new ImportPublicationOutcomeUnknownException(
                    "Re-embed job publication outcome is not attributable to this package import.",
                    writeFailure);
            }
        }
    }

    private static bool ReembedJobsEquivalent(JobRecord? left, JobRecord right) =>
        left != null &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        left.JobType == right.JobType &&
        string.Equals(left.Profile, right.Profile, StringComparison.Ordinal) &&
        string.Equals(left.LibraryId, right.LibraryId, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.InputJson, right.InputJson, StringComparison.Ordinal) &&
        left.Status == right.Status &&
        string.Equals(left.PipelineState, right.PipelineState, StringComparison.Ordinal) &&
        left.CreatedAt == right.CreatedAt &&
        string.Equals(left.ItemsLabel, right.ItemsLabel, StringComparison.Ordinal);

    private static AttemptedReembedJob CreateReembedJob(string libraryId,
                                                          string version,
                                                          string? profile)
    {
        var options = new ReembedOptions();
        var jobRecord = new JobRecord
                            {
                                 Id = Guid.NewGuid().ToString(),
                                 JobType = JobType.Reembed,
                                 Profile = profile,
                                 LibraryId = libraryId,
                                 Version = version,
                                 InputJson = JsonSerializer.Serialize(options),
                                 CreatedAt = UtcNowAtMongoPrecision(),
                                 Status = JobStatus.Queued,
                                 ItemsLabel = ReembedItemsLabel
                             };
        return new AttemptedReembedJob(jobRecord, version);
    }

    private static DateTime UtcNowAtMongoPrecision()
    {
        DateTime utcNow = DateTime.UtcNow;
        long normalizedTicks = utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMillisecond;
        return new DateTime(normalizedTicks, DateTimeKind.Utc);
    }

    private static string BuildRecommendedFollowUp(IReadOnlyList<string> reembedJobIds,
                                                     IReadOnlyList<string> manualReembedVersions,
                                                     long bytesFreed,
                                                     bool overwroteAny)
    {
        var parts = new List<string>();
        if (reembedJobIds.Count > 0)
            parts.Add($"Re-embed in progress (jobs: {string.Join(", ", reembedJobIds)}); monitor with get_reembed_status.");
        if (manualReembedVersions.Count > 0)
        {
            parts.Add(
                $"Re-embed is required for preserved replacement versions {string.Join(", ", manualReembedVersions)}; run reembed_library for each version.");
        }
        if (overwroteAny && bytesFreed > 0)
            parts.Add($"Run compact_collections to reclaim {bytesFreed} bytes freed by overwrite.");
        return string.Join(FollowUpSeparator, parts);
    }

    private sealed record ValidatedImportPackage(
        LibraryRecord Library,
        IReadOnlyDictionary<string, SourceDocumentRecord> Sources,
        IReadOnlyDictionary<SubjectCatalogKey, SubjectCatalogRecord> Catalogs,
        IReadOnlyDictionary<string, ValidatedVersionPackage> Versions);

    private sealed record ValidatedVersionPackage(
        LibraryVersionRecord VersionRecord,
        LibraryProfile? Profile,
        LibraryIndex? Index,
        VersionDiffRecord? Diff,
        IReadOnlyList<ExcludedSymbol> ExcludedSymbols,
        IReadOnlyList<PageRecord> Pages,
        IReadOnlyList<DocChunk> Chunks,
        IReadOnlyList<DocumentRevisionRecord> DocumentRevisions,
        IReadOnlyList<SubjectAssignmentRecord> SubjectAssignments,
        IReadOnlyList<Bm25Shard> Bm25Shards,
        IReadOnlyList<string> Bm25GridFsPaths);

    private sealed record AttemptedReembedJob(JobRecord Record, string Version);

    private sealed record OwnedImportCatalog(SubjectCatalogRecord Candidate, string ImportOperationId);

    private sealed class ImportPublicationOutcomeUnknownException : InvalidOperationException
    {
        internal ImportPublicationOutcomeUnknownException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    #region VersionWriteLog nested class

    private sealed class VersionWriteLog
    {
        public List<string> PageIds { get; } = new();
        public List<string> ChunkIds { get; } = new();
        public bool VersionClaimed { get; set; }
        public string? VersionId { get; set; }
        public string? ProfileId { get; set; }
        public string? IndexId { get; set; }
        public bool DiffWritten { get; set; }
        public List<string> ExcludedIds { get; } = new();
        public List<string> ShardIds { get; } = new();
        public List<string> GridFsIds { get; } = new();
        public HashSet<string> ScanRunIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CatalogScanRunIds { get; } = new(StringComparer.Ordinal);
        public List<OwnedImportCatalog> OwnedCatalogs { get; } = new();
    }

    #endregion
}
