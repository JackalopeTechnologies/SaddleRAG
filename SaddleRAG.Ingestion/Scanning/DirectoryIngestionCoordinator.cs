// DirectoryIngestionCoordinator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Publishes a complete manual directory snapshot through one atomic boundary.</summary>
public sealed class DirectoryIngestionCoordinator : IDirectoryIngestionCoordinator
{
    public DirectoryIngestionCoordinator(RepositoryFactory repositoryFactory,
                                         IDirectoryIngestionPipeline pipeline,
                                         ILibraryDeletionService deletionService,
                                         ILogger<DirectoryIngestionCoordinator> logger,
                                         ILibraryIngestionModeLeaseManager modeLeaseManager)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        mRepositoryFactory = repositoryFactory;
        mPipeline = pipeline;
        mDeletionService = deletionService;
        mLogger = logger;
        mModeLeaseManager = modeLeaseManager;
    }

    private readonly ILibraryDeletionService mDeletionService;
    private readonly ILogger<DirectoryIngestionCoordinator> mLogger;
    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;
    private readonly IDirectoryIngestionPipeline mPipeline;
    private readonly RepositoryFactory mRepositoryFactory;

    public async Task<DirectoryIngestionResult> RunAsync(DirectoryIngestionRequest request,
                                                         Action<DirectoryScanProgress>? onProgress,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        DirectoryLibraryDefinition definition = request.Definition;
        DirectoryIngestionResult result;
        if (definition.BindingStatus != DirectoryLibraryBindingStatus.Bound)
        {
            result = Failed(request,
                            DirectoryScanReasonCodes.LibraryNotBound,
                            LibraryNotBoundDetail);
        }
        else
        {
            result = await RunBoundAsync(request, definition, onProgress, ct);
        }

        return result;
    }

    private async Task<DirectoryIngestionResult> RunBoundAsync(
        DirectoryIngestionRequest request,
        DirectoryLibraryDefinition definition,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(request.Profile);
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(request.Profile);
        ILibraryIngestionModeLease? modeLease = await mModeLeaseManager.TryAcquireAsync(
                                                    request.Profile,
                                                    definition.Id,
                                                    LibraryIngestionMode.Directory,
                                                    ct);
        DirectoryIngestionResult result;
        if (modeLease == null)
        {
            result = Failed(request, DirectoryScanReasonCodes.ScanFailed, ModeLeaseBusyDetail);
        }
        else
        {
            await using(modeLease)
            {
                using CancellationTokenSource operation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, modeLease.OwnershipLostToken);
                result = await RunUnderModeFenceAsync(request,
                                                      definition,
                                                      libraries,
                                                      sources,
                                                      modeLease,
                                                      onProgress,
                                                      operation.Token);
            }
        }

        return result;
    }

    private async Task<DirectoryIngestionResult> RunUnderModeFenceAsync(
        DirectoryIngestionRequest request,
        DirectoryLibraryDefinition definition,
        ILibraryRepository libraries,
        ISourceDocumentRepository sources,
        ILibraryIngestionModeLease modeLease,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        DirectoryLibraryDefinition? persistedDefinition =
            await sources.GetDirectoryDefinitionAsync(definition.Id, ct);
        DirectoryIngestionResult result;
        if (persistedDefinition == null)
        {
            await modeLease.TryAbandonReservationAsync(CancellationToken.None);
            result = Failed(request, DirectoryScanReasonCodes.LibraryNotRegistered, LibraryNotRegisteredDetail);
        }
        else
        {
            bool committed = await modeLease.TryCommitAsync(ct);
            if (!committed)
            {
                result = Failed(request, DirectoryScanReasonCodes.ScanFailed, ModeLeaseLostDetail);
            }
            else
            {
                result = await AcquirePublicationLeaseAndRunAsync(request,
                                                                  definition,
                                                                  libraries,
                                                                  sources,
                                                                  modeLease,
                                                                  onProgress,
                                                                  ct);
            }
        }

        return result;
    }

    private async Task<DirectoryIngestionResult> AcquirePublicationLeaseAndRunAsync(
        DirectoryIngestionRequest request,
        DirectoryLibraryDefinition definition,
        ILibraryRepository libraries,
        ISourceDocumentRepository sources,
        ILibraryIngestionModeLease modeLease,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        IDirectoryPublicationLease? publicationLease =
            await sources.TryAcquireDirectoryPublicationLeaseAsync(definition.Id,
                                                                    definition.RegistrationRevision,
                                                                    definition.RegistrationIncarnationId,
                                                                    request.ScanRunId,
                                                                    definition.LastPublishedVersion,
                                                                    ct);
        DirectoryIngestionResult result;
        if (publicationLease == null)
        {
            result = Failed(request, DirectoryScanReasonCodes.ScanFailed, PublicationLeaseBusyDetail);
        }
        else
        {
            await using(publicationLease)
            {
                result = await RunUnderLeaseAsync(request,
                                                  definition,
                                                  libraries,
                                                  sources,
                                                  modeLease,
                                                  publicationLease,
                                                  onProgress,
                                                  ct);
            }
        }

        return result;
    }

    private async Task<DirectoryIngestionResult> RunUnderLeaseAsync(
        DirectoryIngestionRequest request,
        DirectoryLibraryDefinition definition,
        ILibraryRepository libraries,
        ISourceDocumentRepository sources,
        ILibraryIngestionModeLease modeLease,
        IDirectoryPublicationLease publicationLease,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct)
    {
        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(ct, publicationLease.OwnershipLostToken);
        await RequireActivePublicationLeaseAsync(publicationLease, operation.Token);
        LibraryRecord? library = await libraries.GetLibraryAsync(request.LibraryId, operation.Token);
        string? previousVersion = CurrentVersion(library);
        LibraryVersionRecord buildingVersion = CreateVersion(request,
                                                              previousVersion,
                                                              VersionPublicationState.Building,
                                                              publicationError: null,
                                                              pipelineResult: null);
        DirectoryVersionClaimResult claim = await libraries.TryClaimDirectoryVersionAsync(buildingVersion,
                                                                                            operation.Token);
        DirectoryIngestionResult result;
        if (claim.Status == DirectoryVersionClaimStatus.Acquired)
        {
            result = await RunCandidateAsync(request,
                                             definition,
                                             library,
                                             buildingVersion,
                                             claim.RequiresCleanup,
                                             libraries,
                                             sources,
                                             modeLease,
                                             publicationLease,
                                             onProgress,
                                             operation.Token);
        }
        else
        {
            result = ResolveClaim(request, claim.Status);
        }

        return result;
    }

    private async Task<DirectoryIngestionResult> RunCandidateAsync(DirectoryIngestionRequest request,
                                                                   DirectoryLibraryDefinition definition,
                                                                   LibraryRecord? library,
                                                                   LibraryVersionRecord buildingVersion,
                                                                   bool requiresCleanup,
                                                                   ILibraryRepository libraries,
                                                                   ISourceDocumentRepository sources,
                                                                   ILibraryIngestionModeLease modeLease,
                                                                   IDirectoryPublicationLease publicationLease,
                                                                   Action<DirectoryScanProgress>? onProgress,
                                                                   CancellationToken ct)
    {
        string? previousVersion = CurrentVersion(library);
        var priorVersions = library?.AllVersions.ToList() ?? [];
        DirectoryIngestionPipelineResult? pipelineResult = null;
        var publicationMetadataState = PublicationMetadataWriteState.NotAttempted;
        bool cleanupAlreadyBegun = false;
        bool executePipeline = true;
        DirectoryIngestionResult result = ResolveClaim(request, DirectoryVersionClaimStatus.InProgress);
        try
        {
            if (requiresCleanup)
            {
                cleanupAlreadyBegun = await libraries.TryBeginDirectoryVersionCleanupAsync(request.LibraryId,
                                                                                            request.Version,
                                                                                            request.ScanRunId,
                                                                                            ct);
                executePipeline = cleanupAlreadyBegun;
            }

            if (requiresCleanup && executePipeline)
            {
                await mDeletionService.DeleteScanCandidateUnderLeaseAsync(request.Profile,
                                                                           request.LibraryId,
                                                                           request.Version,
                                                                           publicationLease,
                                                                           modeLease,
                                                                           ct);
                cleanupAlreadyBegun = false;
                DirectoryVersionClaimResult retryClaim =
                    await libraries.TryClaimDirectoryVersionAsync(buildingVersion, ct);
                executePipeline = retryClaim.Status == DirectoryVersionClaimStatus.Acquired;
                if (!executePipeline)
                    result = ResolveClaim(request, retryClaim.Status);
            }

            if (executePipeline)
            {
                pipelineResult = await mPipeline.ExecuteAsync(request, onProgress, ct);
                await RequireActivePublicationLeaseAsync(publicationLease, ct);
                await sources.PublishCandidateScanRunAsync(request.LibraryId,
                                                           request.Version,
                                                           request.ScanRunId,
                                                           ct);
                await PublishSubjectCatalogAsync(request,
                                                 pipelineResult.SubjectTaxonomyVersion,
                                                 publicationLease,
                                                 ct);

                await RequireActivePublicationLeaseAsync(publicationLease, ct);
                LibraryVersionRecord publishedVersion = CreateVersion(request,
                                                                       previousVersion,
                                                                       VersionPublicationState.Published,
                                                                       publicationError: null,
                                                                       pipelineResult);
                bool versionPublished = await libraries.TryPublishDirectoryVersionAsync(publishedVersion,
                                                                                         request.ScanRunId,
                                                                                         ct);
                if (!versionPublished)
                    throw new InvalidOperationException(PublicationLeaseLostDetail);

                await RequireActivePublicationLeaseAsync(publicationLease, ct);
                publicationMetadataState = PublicationMetadataWriteState.Ambiguous;
                bool publicationMetadataWritten = await sources.TryUpdateDirectoryPublicationAsync(
                                                      publicationLease,
                                                      definition.LastPublishedVersion,
                                                      request.QueuedAt.UtcDateTime,
                                                      request.Version,
                                                      ct);
                publicationMetadataState = publicationMetadataWritten
                    ? PublicationMetadataWriteState.Written
                    : PublicationMetadataWriteState.NotWritten;
                if (!publicationMetadataWritten)
                    throw new InvalidOperationException(PublicationLeaseLostDetail);

                await RequireActivePublicationLeaseAsync(publicationLease, ct);
                library ??= CreateLibrary(definition);
                PublishLibraryPointer(library, request.Version);
                await libraries.UpsertLibraryAsync(library, ct);
                result = Completed(request, pipelineResult);
            }
        }
        catch(OperationCanceledException ex)
        {
            RestoreLibraryPointer(library, previousVersion, priorVersions);
            switch(modeLease.OwnershipLostToken.IsCancellationRequested,
                   publicationLease.OwnershipLostToken.IsCancellationRequested)
            {
                case (true, _):
                    result = Failed(request, DirectoryScanReasonCodes.ScanFailed, ModeLeaseLostDetail);
                    break;
                case (false, true):
                    result = Failed(request, DirectoryScanReasonCodes.ScanFailed, PublicationLeaseLostDetail);
                    break;
                default:
                    string detail = SanitizeDetail(ex.Message, definition.RootPath);
                    await MarkFailedPreservingOriginalAsync(request,
                                                             definition,
                                                             modeLease,
                                                             publicationLease,
                                                             publicationMetadataState,
                                                             cleanupAlreadyBegun,
                                                             libraries,
                                                             previousVersion,
                                                             pipelineResult,
                                                             detail,
                                                             ex);
                    throw;
            }
        }
        catch(DirectoryIngestionException ex)
        {
            RestoreLibraryPointer(library, previousVersion, priorVersions);
            string detail = SanitizeDetail(ex.Detail, definition.RootPath);
            string publicationError = $"{ex.ReasonCode}: {detail}";
            await MarkFailedPreservingOriginalAsync(request,
                                                    definition,
                                                    modeLease,
                                                    publicationLease,
                                                    publicationMetadataState,
                                                    cleanupAlreadyBegun,
                                                    libraries,
                                                    previousVersion,
                                                    pipelineResult,
                                                    publicationError,
                                                    ex);
            result = Failed(request, ex.ReasonCode, detail, ex.RelativePath);
        }
        catch(Exception ex)
        {
            RestoreLibraryPointer(library, previousVersion, priorVersions);
            string detail = SanitizeDetail(ex.Message, definition.RootPath);
            await MarkFailedPreservingOriginalAsync(request,
                                                    definition,
                                                    modeLease,
                                                    publicationLease,
                                                    publicationMetadataState,
                                                    cleanupAlreadyBegun,
                                                    libraries,
                                                    previousVersion,
                                                    pipelineResult,
                                                    detail,
                                                    ex);
            result = Failed(request, DirectoryScanReasonCodes.ScanFailed, detail);
        }
        return result;
    }

    private async Task PublishSubjectCatalogAsync(DirectoryIngestionRequest request,
                                                   string? taxonomyVersion,
                                                   IDirectoryPublicationLease publicationLease,
                                                   CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(taxonomyVersion))
        {
            await RequireActivePublicationLeaseAsync(publicationLease, ct);
            ISubjectCatalogRepository catalogs = mRepositoryFactory.GetSubjectCatalogRepository(request.Profile);
            bool catalogPublished = await catalogs.TryPublishCandidateAsync(request.LibraryId,
                                                                             taxonomyVersion,
                                                                             request.ScanRunId,
                                                                             ct);
            if (!catalogPublished)
                throw new InvalidOperationException(PublicationLeaseLostDetail);
        }
    }

    private static async Task RequireActivePublicationLeaseAsync(IDirectoryPublicationLease publicationLease,
                                                                  CancellationToken ct)
    {
        bool renewed = await publicationLease.TryRenewAsync(ct);
        if (!renewed)
            throw new InvalidOperationException(PublicationLeaseLostDetail);
    }

    private async Task MarkFailedPreservingOriginalAsync(DirectoryIngestionRequest request,
                                                           DirectoryLibraryDefinition definition,
                                                           ILibraryIngestionModeLease modeLease,
                                                           IDirectoryPublicationLease publicationLease,
                                                           PublicationMetadataWriteState publicationMetadataState,
                                                           bool cleanupAlreadyBegun,
                                                           ILibraryRepository libraries,
                                                           string? previousVersion,
                                                           DirectoryIngestionPipelineResult? pipelineResult,
                                                           string publicationError,
                                                           Exception originalException)
    {
        var failures = new List<Exception>();
        using CancellationTokenSource ownership = CancellationTokenSource.CreateLinkedTokenSource(
                                                      modeLease.OwnershipLostToken,
                                                      publicationLease.OwnershipLostToken);
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(request.Profile);
        bool restoreRequired = publicationMetadataState is PublicationMetadataWriteState.Written
            or PublicationMetadataWriteState.Ambiguous;
        bool metadataSafeForCleanup = !restoreRequired ||
                                      await TryRestorePublicationMetadataAsync(
                                          sources,
                                          request,
                                          definition,
                                          publicationLease,
                                          allowExactPriorObservation: publicationMetadataState ==
                                                                      PublicationMetadataWriteState.Ambiguous,
                                          ownership.Token,
                                          failures);

        bool cleanupOwned = cleanupAlreadyBegun && metadataSafeForCleanup;
        if (metadataSafeForCleanup && !cleanupOwned)
        {
            try
            {
                cleanupOwned = await libraries.TryBeginDirectoryVersionCleanupAsync(request.LibraryId,
                                                                                     request.Version,
                                                                                     request.ScanRunId,
                                                                                     ownership.Token);
            }
            catch(Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (cleanupOwned)
        {
            await TryRollbackSubjectCatalogAsync(request,
                                                 pipelineResult,
                                                 ownership.Token,
                                                 failures);
            await TryFailureStepAsync(async () =>
                                      {
                                          await mDeletionService.DeleteScanCandidateUnderLeaseAsync(
                                              request.Profile,
                                              request.LibraryId,
                                              request.Version,
                                              publicationLease,
                                              modeLease,
                                              ownership.Token);
                                      },
                                      failures);
        }

        if (cleanupOwned)
        {
            LibraryVersionRecord failed = CreateVersion(request,
                                                        previousVersion,
                                                        VersionPublicationState.Failed,
                                                        publicationError,
                                                        pipelineResult);
            await TryFailureStepAsync(async () =>
                                      {
                                          await libraries.TryRecordDirectoryVersionFailureAsync(
                                               failed,
                                               request.ScanRunId,
                                               ownership.Token);
                                      },
                                      failures);
        }

        if (failures.Count > 0)
        {
            var cleanupFailure = new AggregateException(CandidateCleanupFailureMessage, failures);
            originalException.Data[CandidateCleanupFailureDataKey] = cleanupFailure.ToString();
            mLogger.LogError(cleanupFailure,
                             "Directory candidate cleanup failed for {LibraryId} v{Version}; preserving the original failure: {OriginalFailure}",
                             request.LibraryId,
                             request.Version,
                             publicationError);
        }
    }

    private async Task TryRollbackSubjectCatalogAsync(DirectoryIngestionRequest request,
                                                       DirectoryIngestionPipelineResult? pipelineResult,
                                                       CancellationToken ct,
                                                       List<Exception> failures)
    {
        string? taxonomyVersion = pipelineResult?.SubjectTaxonomyVersion;
        if (!string.IsNullOrEmpty(taxonomyVersion))
        {
            await TryFailureStepAsync(async () =>
                                      {
                                          ISubjectCatalogRepository catalogs =
                                              mRepositoryFactory.GetSubjectCatalogRepository(request.Profile);
                                          await catalogs.TryRollbackCandidatePublicationAsync(
                                              request.LibraryId,
                                              taxonomyVersion,
                                              request.ScanRunId,
                                              ct);
                                      },
                                      failures);
        }
    }

    private static async Task<bool> TryRestorePublicationMetadataAsync(
        ISourceDocumentRepository sources,
        DirectoryIngestionRequest request,
        DirectoryLibraryDefinition definition,
        IDirectoryPublicationLease publicationLease,
        bool allowExactPriorObservation,
        CancellationToken ct,
        List<Exception> failures)
    {
        bool result = false;
        Exception? restoreFailure = null;
        Exception? observationFailure = null;
        try
        {
            result = await sources.TryRestoreDirectoryPublicationAsync(
                         publicationLease,
                         request.Version,
                         definition.LastPublishedAtUtc,
                         definition.LastPublishedVersion,
                         ct);
        }
        catch(Exception ex)
        {
            restoreFailure = ex;
        }

        if (allowExactPriorObservation)
        {
            result = false;
            try
            {
                result = await ConfirmExactPriorPublicationAsync(sources,
                                                                  definition,
                                                                  publicationLease,
                                                                  ct);
            }
            catch(Exception ex)
            {
                observationFailure = ex;
            }
        }

        if (restoreFailure != null)
            failures.Add(restoreFailure);
        if (observationFailure != null)
            failures.Add(observationFailure);
        if (!result && restoreFailure == null && observationFailure == null)
            failures.Add(new InvalidOperationException(PublicationMetadataRestoreFailureMessage));

        return result;
    }

    private static async Task<bool> ConfirmExactPriorPublicationAsync(
        ISourceDocumentRepository sources,
        DirectoryLibraryDefinition definition,
        IDirectoryPublicationLease publicationLease,
        CancellationToken ct)
    {
        await RequireActivePublicationLeaseAsync(publicationLease, ct);
        DirectoryLibraryDefinition? current = await sources.GetDirectoryDefinitionAsync(definition.Id, ct);
        return current != null && IsExactPriorPublicationUnderLease(current,
                                                                     definition,
                                                                     publicationLease);
    }

    private static bool IsExactPriorPublicationUnderLease(DirectoryLibraryDefinition current,
                                                           DirectoryLibraryDefinition prior,
                                                           IDirectoryPublicationLease publicationLease) =>
        string.Equals(current.Id, prior.Id, StringComparison.Ordinal) &&
        current.RegistrationRevision == publicationLease.RegistrationRevision &&
        string.Equals(current.RegistrationIncarnationId,
                      publicationLease.RegistrationIncarnationId,
                      StringComparison.Ordinal) &&
        string.Equals(current.PublicationLeaseScanRunId,
                      publicationLease.ScanRunId,
                      StringComparison.Ordinal) &&
        current.PublicationLeaseRegistrationRevision == publicationLease.RegistrationRevision &&
        current.PendingRenameOperationId == null &&
        string.Equals(current.LastPublishedVersion,
                      prior.LastPublishedVersion,
                      StringComparison.Ordinal) &&
        current.LastPublishedAtUtc == prior.LastPublishedAtUtc;

    private static async Task TryFailureStepAsync(Func<Task> operation, List<Exception> failures)
    {
        try
        {
            await operation();
        }
        catch(Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static LibraryVersionRecord CreateVersion(DirectoryIngestionRequest request,
                                                       string? previousVersion,
                                                       VersionPublicationState state,
                                                       string? publicationError,
                                                       DirectoryIngestionPipelineResult? pipelineResult) =>
        new()
            {
                Id = $"{request.LibraryId}/{request.Version}",
                LibraryId = request.LibraryId,
                Version = request.Version,
                ScrapedAt = request.QueuedAt.UtcDateTime,
                PageCount = pipelineResult?.PagesIndexed ?? 0,
                ChunkCount = pipelineResult?.ChunksIndexed ?? 0,
                EmbeddingProviderId = pipelineResult?.EmbeddingProviderId ?? string.Empty,
                EmbeddingModelName = pipelineResult?.EmbeddingModelName ?? string.Empty,
                EmbeddingDimensions = pipelineResult?.EmbeddingDimensions ?? 0,
                ClassifierBackend = pipelineResult?.ClassifierBackend,
                ClassifierModel = pipelineResult?.ClassifierModel,
                SubjectTaxonomyVersion = pipelineResult?.SubjectTaxonomyVersion,
                PreviousVersion = previousVersion,
                PublicationState = state,
                PublicationError = publicationError,
                ScanRunId = request.ScanRunId,
                RegistrationRevision = request.Definition.RegistrationRevision,
                CleanupInProgress = false
            };

    private static LibraryRecord CreateLibrary(DirectoryLibraryDefinition definition)
    {
        string? requestedName = definition.Name;
        string? requestedHint = definition.Hint;
        string name = string.IsNullOrWhiteSpace(requestedName) ? definition.Id : requestedName;
        string hint = string.IsNullOrWhiteSpace(requestedHint) ? DirectoryLibraryHint : requestedHint;
        var result = new LibraryRecord
                         {
                             Id = definition.Id,
                             Name = name,
                             Hint = hint,
                             CurrentVersion = string.Empty,
                             AllVersions = []
                         };
        return result;
    }

    private static void PublishLibraryPointer(LibraryRecord library, string version)
    {
        if (!library.AllVersions.Contains(version, StringComparer.Ordinal))
            library.AllVersions.Add(version);
        library.CurrentVersion = version;
    }

    private static void RestoreLibraryPointer(LibraryRecord? library,
                                              string? previousVersion,
                                              IReadOnlyList<string> priorVersions)
    {
        if (library != null)
        {
            library.CurrentVersion = previousVersion ?? string.Empty;
            library.AllVersions.Clear();
            library.AllVersions.AddRange(priorVersions);
        }
    }

    private static string? CurrentVersion(LibraryRecord? library)
    {
        string? result = library?.CurrentVersion;
        if (string.IsNullOrWhiteSpace(result))
            result = null;
        return result;
    }

    private static DirectoryIngestionResult ResolveClaim(DirectoryIngestionRequest request,
                                                          DirectoryVersionClaimStatus status)
    {
        string resultStatus = status == DirectoryVersionClaimStatus.AlreadyPublished
                                  ? DirectoryScanVersionProvider.AlreadyScannedTodayStatus
                                  : DirectoryScanVersionProvider.ScanInProgressStatus;
        var result = new DirectoryIngestionResult(resultStatus,
                                                  request.LibraryId,
                                                  request.Version);
        return result;
    }

    private static DirectoryIngestionResult Completed(DirectoryIngestionRequest request,
                                                       DirectoryIngestionPipelineResult pipelineResult) =>
        new(DirectoryIngestionStatuses.Completed,
            request.LibraryId,
            request.Version,
            pipelineResult.DocumentsProcessed,
            pipelineResult.PagesIndexed,
            pipelineResult.ChunksIndexed);

    private static DirectoryIngestionResult Failed(DirectoryIngestionRequest request,
                                                    string reasonCode,
                                                    string detail,
                                                    string? relativePath = null)
    {
        var result = new DirectoryIngestionResult(DirectoryIngestionStatuses.Failed,
                                                  request.LibraryId,
                                                  request.Version,
                                                  ReasonCode: reasonCode,
                                                  Detail: detail);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            result = result with
                         {
                             FileFailures = [new DirectoryScanFileFailure(relativePath,
                                                                          reasonCode,
                                                                          detail)]
                         };
        }

        return result;
    }

    private static string SanitizeDetail(string detail, string registeredRoot)
    {
        var result = detail;
        if (!string.IsNullOrWhiteSpace(registeredRoot))
        {
            result = result.Replace(registeredRoot,
                                    RegisteredRootReplacement,
                                    StringComparison.OrdinalIgnoreCase);
            string alternateRoot = registeredRoot.Replace('\\', '/');
            result = result.Replace(alternateRoot,
                                    RegisteredRootReplacement,
                                    StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static void ValidateRequest(DirectoryIngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(request.Version);
        ArgumentException.ThrowIfNullOrEmpty(request.ScanRunId);
        ArgumentNullException.ThrowIfNull(request.Definition);
        if (!request.LibraryId.Equals(request.Definition.Id, StringComparison.Ordinal))
            throw new ArgumentException("The captured directory definition must match the requested library.",
                                        nameof(request));
    }

    private const string CandidateCleanupFailureDataKey = "DirectoryCandidateCleanupFailure";
    private const string CandidateCleanupFailureMessage = "One or more directory candidate cleanup steps failed.";
    private const string DirectoryLibraryHint = "Documents from a manually registered local directory.";
    private const string LibraryNotBoundDetail = "The directory library is not bound to a directory on this machine.";
    private const string LibraryNotRegisteredDetail = "Register the directory library before scanning it.";
    private const string ModeLeaseBusyDetail =
        "The library identifier is owned by web ingestion or another directory lifecycle operation.";
    private const string ModeLeaseLostDetail = "The directory scan no longer owns its ingestion-mode lease.";
    private const string PublicationLeaseBusyDetail =
        "The directory library is currently being scanned or deleted. Try again after that operation finishes.";
    private const string PublicationLeaseLostDetail = "The directory scan no longer owns its publication lease.";
    private const string PublicationMetadataRestoreFailureMessage =
        "Directory publication metadata still references the failed candidate; candidate cleanup was stopped.";
    private const string RegisteredRootReplacement = "<registered-root>";

    private enum PublicationMetadataWriteState
    {
        NotAttempted,
        NotWritten,
        Written,
        Ambiguous
    }
}
