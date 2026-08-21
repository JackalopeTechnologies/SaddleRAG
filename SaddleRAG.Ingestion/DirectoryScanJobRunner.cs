// DirectoryScanJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Ingestion;

/// <summary>
///     Persists and runs one explicitly requested manual directory scan.
///     Construction, service startup, and source-directory changes never
///     call <see cref="QueueAsync" /> and therefore never start a scan.
/// </summary>
public sealed class DirectoryScanJobRunner : IDirectoryScanJobQueue
{
    public DirectoryScanJobRunner(IDirectoryIngestionCoordinator coordinator,
                                  DirectoryScanVersionProvider versionProvider,
                                  RepositoryFactory repositoryFactory,
                                  IJobCancellationRegistry cancellationRegistry,
                                  IHostApplicationLifetime lifetime,
                                  IDirectoryDocumentCapabilityPreflight capabilityPreflight,
                                  ILogger<DirectoryScanJobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(versionProvider);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(cancellationRegistry);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(capabilityPreflight);
        ArgumentNullException.ThrowIfNull(logger);
        mCoordinator = coordinator;
        mVersionProvider = versionProvider;
        mRepositoryFactory = repositoryFactory;
        mCancellationRegistry = cancellationRegistry;
        mApplicationStopping = lifetime.ApplicationStopping;
        mCapabilityPreflight = capabilityPreflight;
        mLogger = logger;
    }

    private readonly CancellationToken mApplicationStopping;
    private readonly IDirectoryDocumentCapabilityPreflight mCapabilityPreflight;
    private readonly IJobCancellationRegistry mCancellationRegistry;
    private readonly IDirectoryIngestionCoordinator mCoordinator;
    private readonly ILogger<DirectoryScanJobRunner> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly DirectoryScanVersionProvider mVersionProvider;

    public async Task<DirectoryScanQueueResult> QueueAsync(string libraryId,
                                                           string? profile = null,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ISourceDocumentRepository sourceDocuments = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sourceDocuments.GetDirectoryDefinitionAsync(libraryId, ct);
        DirectoryScanQueueResult result;
        switch(definition)
        {
            case null:
                result = new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Failed,
                                                      libraryId,
                                                      string.Empty,
                                                      ReasonCode: DirectoryScanReasonCodes.LibraryNotRegistered,
                                                      Detail: LibraryNotRegisteredDetail);
                break;
            case { BindingStatus: not DirectoryLibraryBindingStatus.Bound }:
                result = new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Failed,
                                                      libraryId,
                                                      string.Empty,
                                                      ReasonCode: DirectoryScanReasonCodes.LibraryNotBound,
                                                      Detail: LibraryNotBoundDetail);
                break;
            default:
                result = await QueueWithPreflightAsync(libraryId, profile, definition, ct);
                break;
        }

        return result;
    }

    private async Task<DirectoryScanQueueResult> QueueWithPreflightAsync(
        string libraryId,
        string? profile,
        DirectoryLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        DocumentCapabilityPreflightResult? preflight = await TryEvaluatePreflightAsync(definition,
                                                                                       libraryId,
                                                                                       cancellationToken);
        DirectoryScanQueueResult result = preflight == null
            ? PreflightFaultedResult(libraryId)
            : preflight.Allowed
                ? await QueueBoundLibraryAsync(libraryId, profile, definition, cancellationToken)
                : BlockedResult(libraryId, preflight);
        return result;
    }

    private async Task<DocumentCapabilityPreflightResult?> TryEvaluatePreflightAsync(
        DirectoryLibraryDefinition definition,
        string libraryId,
        CancellationToken cancellationToken)
    {
        DocumentCapabilityPreflightResult? result;
        try
        {
            result = await mCapabilityPreflight.EvaluateAsync(definition, cancellationToken);
        }
        catch(Exception error)
        {
            // The preflight walks the filesystem and probes the user-managed Docling
            // endpoint on the request thread. A fault there — a probe error, or a probe
            // interrupted before it reaches a verdict — must surface as a bounded,
            // reported queue failure, never an unhandled exception the host renders as a
            // raw 500. Docling being unavailable is an expected, recovered condition, so
            // this is a Warning rather than an Error.
            mLogger.LogWarning(error,
                               "The document scanner preflight for {LibraryId} did not reach a verdict; reporting a bounded scan-queue failure.",
                               libraryId);
            result = null;
        }

        return result;
    }

    private static DirectoryScanQueueResult PreflightFaultedResult(string libraryId) =>
        new(DirectoryScanQueueStatuses.Failed,
            libraryId,
            string.Empty,
            ReasonCode: DirectoryScanReasonCodes.ScannerPreflightFailed,
            Detail: ScannerPreflightFailedDetail);

    private async Task<DirectoryScanQueueResult> QueueBoundLibraryAsync(
        string libraryId,
        string? profile,
        DirectoryLibraryDefinition definition,
        CancellationToken cancellationToken)
    {
        DirectoryScanVersion captured = mVersionProvider.Capture();
        string jobId = Guid.NewGuid().ToString("N");
        var request = new DirectoryIngestionRequest
                          {
                              LibraryId = libraryId,
                              Version = captured.Value,
                              QueuedAt = captured.QueuedAt,
                              ScanRunId = jobId,
                              Definition = CaptureDefinition(definition),
                              Profile = profile
                          };
        JobRecord job = CreateQueuedJob(jobId, request);
        IJobRepository jobs = mRepositoryFactory.GetJobRepository(profile);
        await jobs.UpsertAsync(job, cancellationToken);
        _ = Task.Run(() => RunAsync(job, request, definition.RootPath), mApplicationStopping);
        return new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Queued,
                                            libraryId,
                                            captured.Value,
                                            jobId);
    }

    private static DirectoryScanQueueResult BlockedResult(
        string libraryId,
        DocumentCapabilityPreflightResult preflight)
    {
        DoclingCapabilityStatus? status = preflight.Status;
        return new DirectoryScanQueueResult(DirectoryScanQueueStatuses.Failed,
                                            libraryId,
                                            string.Empty,
                                            ReasonCode: status?.ReasonCode,
                                            Detail: status?.Detail,
                                            OfficialInstallUrl: preflight.OfficialInstallUrl,
                                            OfficialReleaseUrl: preflight.OfficialReleaseUrl);
    }

    private async Task RunAsync(JobRecord job,
                                DirectoryIngestionRequest request,
                                string registeredRoot)
    {
        IJobRepository jobs = mRepositoryFactory.GetJobRepository(request.Profile);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(mApplicationStopping);
        mCancellationRegistry.Register(job.Id, cts);
        try
        {
            job.Status = JobStatus.Running;
            job.PipelineState = nameof(JobStatus.Running);
            job.StartedAt = DateTime.UtcNow;
            await jobs.UpsertAsync(job, CancellationToken.None);
            DirectoryIngestionResult result = await mCoordinator.RunAsync(
                                                  request,
                                                  progress => PersistProgress(job, progress, jobs),
                                                  cts.Token);
            await PersistResultAsync(job, result, registeredRoot, jobs);
        }
        catch(OperationCanceledException)
        {
            await PersistCancelledAsync(job, jobs);
        }
        catch(Exception ex)
        {
            await PersistFailureAsync(job, SanitizeDetail(ex.Message, registeredRoot), jobs);
            mLogger.LogError(ex,
                             "Manual directory scan job {JobId} failed for {LibraryId}/{Version}",
                             job.Id,
                             request.LibraryId,
                             request.Version);
        }
        finally
        {
            mCancellationRegistry.Unregister(job.Id);
        }
    }

    private void PersistProgress(JobRecord job,
                                 DirectoryScanProgress progress,
                                 IJobRepository jobs)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var sanitized = new DirectoryScanJobProgress
                            {
                                FilesDiscovered = progress.FilesDiscovered,
                                SupportedDocuments = progress.SupportedDocuments,
                                DocumentsCompleted = progress.DocumentsCompleted,
                                CurrentRelativePath = SanitizeRelativePath(progress.CurrentRelativePath)
                            };
        job.DirectoryScanProgress = sanitized;
        job.ItemsProcessed = sanitized.DocumentsCompleted;
        job.ItemsTotal = sanitized.SupportedDocuments;
        job.LastProgressAt = DateTime.UtcNow;
        jobs.UpsertAsync(job, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task PersistResultAsync(JobRecord job,
                                                 DirectoryIngestionResult result,
                                                 string registeredRoot,
                                                 IJobRepository jobs)
    {
        string detail = SanitizeDetail(result.Detail, registeredRoot);
        job.DirectoryScanFailures = SanitizeFailures(result.FileFailures, registeredRoot);
        job.ResultJson = SerializeResult(result, detail);
        switch(result.Status)
        {
            case DirectoryIngestionStatuses.Failed:
                job.Status = JobStatus.Failed;
                job.PipelineState = nameof(JobStatus.Failed);
                job.ErrorMessage = detail;
                job.ErrorCount = Math.Max(1, job.DirectoryScanFailures.Count);
                break;
            case CancelledStatus:
                job.Status = JobStatus.Cancelled;
                job.PipelineState = nameof(JobStatus.Cancelled);
                job.CancelledAt = DateTime.UtcNow;
                break;
            default:
                job.Status = JobStatus.Completed;
                job.PipelineState = nameof(JobStatus.Completed);
                if (job.DirectoryScanProgress == null)
                {
                    job.ItemsProcessed = result.DocumentsProcessed;
                    job.ItemsTotal = result.DocumentsProcessed;
                }

                break;
        }

        job.CompletedAt = DateTime.UtcNow;
        await jobs.UpsertAsync(job, CancellationToken.None);
    }

    private static IReadOnlyList<DirectoryScanFileFailure> SanitizeFailures(
        IReadOnlyList<DirectoryScanFileFailure> failures,
        string registeredRoot)
    {
        var result = new List<DirectoryScanFileFailure>(failures.Count);
        foreach (DirectoryScanFileFailure failure in failures)
        {
            string relativePath = SanitizeRelativePath(failure.RelativePath)
                                  ?? InvalidRelativePathDisplay;
            result.Add(new DirectoryScanFileFailure(relativePath,
                                                    failure.ReasonCode,
                                                    SanitizeDetail(failure.Detail, registeredRoot)));
        }

        return result;
    }

    private static async Task PersistCancelledAsync(JobRecord job, IJobRepository jobs)
    {
        job.Status = JobStatus.Cancelled;
        job.PipelineState = nameof(JobStatus.Cancelled);
        job.CancelledAt = DateTime.UtcNow;
        job.CompletedAt = DateTime.UtcNow;
        await jobs.UpsertAsync(job, CancellationToken.None);
    }

    private static async Task PersistFailureAsync(JobRecord job,
                                                  string detail,
                                                  IJobRepository jobs)
    {
        job.Status = JobStatus.Failed;
        job.PipelineState = nameof(JobStatus.Failed);
        job.ErrorMessage = detail;
        job.CompletedAt = DateTime.UtcNow;
        await jobs.UpsertAsync(job, CancellationToken.None);
    }

    private static JobRecord CreateQueuedJob(string jobId, DirectoryIngestionRequest request) => new()
        {
            Id = jobId,
            JobType = JobType.DirectoryScan,
            Profile = request.Profile,
            LibraryId = request.LibraryId,
            Version = request.Version,
            InputJson = JsonSerializer.Serialize(new
                                                     {
                                                         request.LibraryId,
                                                         request.Version,
                                                         request.QueuedAt,
                                                         request.ScanRunId
                                                     },
                                                 smJsonOptions),
            Status = JobStatus.Queued,
            PipelineState = nameof(JobStatus.Queued),
            ItemsLabel = DocumentsLabel,
            CreatedAt = request.QueuedAt.UtcDateTime
        };

    private static DirectoryLibraryDefinition CaptureDefinition(DirectoryLibraryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var result = definition with
                         {
                             AllowedExtensions = definition.AllowedExtensions.ToArray(),
                             ExclusionPatterns = definition.ExclusionPatterns.ToArray()
                         };
        return result;
    }

    private static string SerializeResult(DirectoryIngestionResult result, string detail) =>
        JsonSerializer.Serialize(new
                                     {
                                         result.Status,
                                         result.LibraryId,
                                         result.Version,
                                         result.DocumentsProcessed,
                                         result.PagesIndexed,
                                         result.ChunksIndexed,
                                         result.ReasonCode,
                                         Detail = detail
                                     },
                                 smJsonOptions);

    private static string? SanitizeRelativePath(string? relativePath)
    {
        string? result = null;
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            string candidate = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            bool invalid = Path.IsPathRooted(candidate)
                           || candidate.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                       .Any(segment => segment == ParentDirectorySegment);
            result = invalid ? InvalidRelativePathDisplay : candidate.TrimStart('/');
        }

        return result;
    }

    private static string SanitizeDetail(string? detail, string registeredRoot)
    {
        string result = string.IsNullOrWhiteSpace(detail) ? ScanFailedDetail : detail;
        if (!string.IsNullOrWhiteSpace(registeredRoot))
            result = result.Replace(registeredRoot, RegisteredRootDisplay, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private const string LibraryNotRegisteredDetail =
        "Register the directory library before requesting a manual scan.";
    private const string LibraryNotBoundDetail =
        "Bind the imported directory library to an explicit local root before requesting a manual scan.";
    private const string ScannerPreflightFailedDetail =
        "The document scanner readiness check could not be completed. Confirm the scanner is running, then request the scan again.";
    private const string DocumentsLabel = "documents";
    private const string CancelledStatus = "CANCELLED";
    private const string InvalidRelativePathDisplay = "(invalid relative path)";
    private const string RegisteredRootDisplay = "[registered root]";
    private const string ScanFailedDetail = "The manual directory scan failed.";
    private const string ParentDirectorySegment = "..";
    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
