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
                                         ILogger<DirectoryIngestionCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(deletionService);
        ArgumentNullException.ThrowIfNull(logger);
        mRepositoryFactory = repositoryFactory;
        mPipeline = pipeline;
        mDeletionService = deletionService;
        mLogger = logger;
    }

    private readonly ILibraryDeletionService mDeletionService;
    private readonly ILogger<DirectoryIngestionCoordinator> mLogger;
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
            ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(request.Profile);
            ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(request.Profile);
            LibraryRecord? library = await libraries.GetLibraryAsync(request.LibraryId, ct);
            string? previousVersion = CurrentVersion(library);
            LibraryVersionRecord buildingVersion = CreateVersion(request,
                                                                  previousVersion,
                                                                  VersionPublicationState.Building,
                                                                  publicationError: null,
                                                                  pipelineResult: null);
            DirectoryVersionClaimResult claim = await libraries.TryClaimDirectoryVersionAsync(buildingVersion, ct);
            result = claim.Status == DirectoryVersionClaimStatus.Acquired
                         ? await RunCandidateAsync(request,
                                                   definition,
                                                   library,
                                                   buildingVersion,
                                                   claim.RequiresCleanup,
                                                   libraries,
                                                   sources,
                                                   onProgress,
                                                   ct)
                         : ResolveClaim(request, claim.Status);
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
                                                                   Action<DirectoryScanProgress>? onProgress,
                                                                   CancellationToken ct)
    {
        string? previousVersion = CurrentVersion(library);
        var priorVersions = library?.AllVersions.ToList() ?? [];
        DirectoryIngestionPipelineResult? pipelineResult = null;
        bool publicationMetadataWritten = false;
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
                await mDeletionService.DeleteVersionAsync(request.Profile,
                                                          request.LibraryId,
                                                          request.Version,
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
                await sources.PublishCandidateScanRunAsync(request.LibraryId,
                                                           request.Version,
                                                           request.ScanRunId,
                                                           ct);
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

                publicationMetadataWritten = await sources.TryUpdateDirectoryPublicationAsync(
                                                 definition.Id,
                                                 definition.RegistrationRevision,
                                                 definition.LastPublishedVersion,
                                                 request.QueuedAt.UtcDateTime,
                                                 request.Version,
                                                 ct);
                library ??= CreateLibrary(definition);
                PublishLibraryPointer(library, request.Version);
                await libraries.UpsertLibraryAsync(library, ct);
                result = Completed(request, pipelineResult);
            }
        }
        catch(OperationCanceledException ex)
        {
            RestoreLibraryPointer(library, previousVersion, priorVersions);
            string detail = SanitizeDetail(ex.Message, definition.RootPath);
            await MarkFailedPreservingOriginalAsync(request,
                                                    definition,
                                                    publicationMetadataWritten,
                                                    cleanupAlreadyBegun,
                                                    libraries,
                                                    previousVersion,
                                                    pipelineResult,
                                                    detail,
                                                    ex);
            throw;
        }
        catch(DirectoryIngestionException ex)
        {
            RestoreLibraryPointer(library, previousVersion, priorVersions);
            string detail = SanitizeDetail(ex.Detail, definition.RootPath);
            string publicationError = $"{ex.ReasonCode}: {detail}";
            await MarkFailedPreservingOriginalAsync(request,
                                                    definition,
                                                    publicationMetadataWritten,
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
                                                    publicationMetadataWritten,
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

    private async Task MarkFailedPreservingOriginalAsync(DirectoryIngestionRequest request,
                                                          DirectoryLibraryDefinition definition,
                                                          bool publicationMetadataWritten,
                                                          bool cleanupAlreadyBegun,
                                                          ILibraryRepository libraries,
                                                          string? previousVersion,
                                                          DirectoryIngestionPipelineResult? pipelineResult,
                                                          string publicationError,
                                                          Exception originalException)
    {
        var failures = new List<Exception>();
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(request.Profile);
        if (publicationMetadataWritten)
        {
            await TryFailureStepAsync(async () =>
                                      {
                                          await sources.TryUpdateDirectoryPublicationAsync(
                                              definition.Id,
                                              definition.RegistrationRevision,
                                              request.Version,
                                              definition.LastPublishedAtUtc,
                                              definition.LastPublishedVersion,
                                              CancellationToken.None);
                                      },
                                      failures);
        }

        bool cleanupOwned = cleanupAlreadyBegun;
        if (!cleanupOwned)
        {
            try
            {
                cleanupOwned = await libraries.TryBeginDirectoryVersionCleanupAsync(request.LibraryId,
                                                                                     request.Version,
                                                                                     request.ScanRunId,
                                                                                     CancellationToken.None);
            }
            catch(Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (cleanupOwned && !cleanupAlreadyBegun)
        {
            await TryFailureStepAsync(async () =>
                                      {
                                          await mDeletionService.DeleteVersionAsync(request.Profile,
                                                                                     request.LibraryId,
                                                                                     request.Version,
                                                                                     CancellationToken.None);
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
                                              CancellationToken.None);
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
    private const string PublicationLeaseLostDetail = "The directory scan no longer owns its publication lease.";
    private const string RegisteredRootReplacement = "<registered-root>";
}
