// ReembedJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Runs reembed jobs in the background, tracking status in the
///     unified <c>jobs</c> MongoDB collection so the caller can poll
///     <c>get_reembed_status</c> without blocking the MCP transport
///     connection. Writes typed <see cref="ReembedOptions" /> and
///     <see cref="ReembedResult" /> blobs into
///     <see cref="JobRecord.InputJson" /> and
///     <see cref="JobRecord.ResultJson" />; consumer code reads via the
///     wrappers in <see cref="ReembedJobPayloads" />.
/// </summary>
public class ReembedJobRunner : IReembedJobDispatcher
{
    public ReembedJobRunner(ReembedService service,
                            RepositoryFactory repositoryFactory,
                            IMonitorBroadcaster broadcaster,
                            IJobCancellationRegistry cancellationRegistry,
                            IHostApplicationLifetime lifetime,
                            ILogger<ReembedJobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(cancellationRegistry);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        mService = service;
        mRepositoryFactory = repositoryFactory;
        mBroadcaster = broadcaster;
        mCancellationRegistry = cancellationRegistry;
        mAppStoppingToken = lifetime.ApplicationStopping;
        mLogger = logger;
    }

    private readonly CancellationToken mAppStoppingToken;
    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IJobCancellationRegistry mCancellationRegistry;
    private readonly ILogger<ReembedJobRunner> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly ReembedService mService;
    private readonly ConcurrentDictionary<JobDispatchKey, byte> mScheduled = new();

    /// <summary>
    ///     Queue a reembed job and kick off background execution.
    ///     Returns the job id immediately.
    /// </summary>
    public async Task<string> QueueAsync(string libraryId,
                                         string version,
                                         ReembedOptions options,
                                         string? profile = null,
                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var jobRepo = mRepositoryFactory.GetJobRepository(profile);
        var jobRecord = new JobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = JobType.Reembed,
                                Profile = profile,
                                LibraryId = libraryId,
                                Version = version,
                                InputJson = JsonSerializer.Serialize(options),
                                Status = JobStatus.Queued,
                                ItemsLabel = ProgressLabel
                            };

        await jobRepo.UpsertAsync(jobRecord, ct);

        TryDispatchPersisted(jobRecord);

        return jobRecord.Id;
    }

    /// <inheritdoc />
    public bool TryDispatchPersisted(JobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateQueuedReembed(record);
        var key = new JobDispatchKey(record.Profile, record.Id);
        bool result = mScheduled.TryAdd(key, value: 0);
        bool canDispatch = result && !mAppStoppingToken.IsCancellationRequested;
        if (canDispatch)
            _ = Task.Run(() => RunScheduledJobAsync(record, key));
        if (result && !canDispatch)
        {
            mScheduled.TryRemove(key, out _);
            result = false;
        }
        return result;
    }

    private async Task RunScheduledJobAsync(JobRecord candidate, JobDispatchKey key)
    {
        try
        {
            var jobRepo = mRepositoryFactory.GetJobRepository(candidate.Profile);
            JobRecord? claimed = await ClaimQueuedJobAsync(candidate, jobRepo);
            if (claimed != null)
                await RunClaimedJobAsync(claimed, jobRepo);
        }
        catch(OperationCanceledException) when(mAppStoppingToken.IsCancellationRequested)
        {
            mLogger.LogInformation("Reembed dispatch stopped before job {JobId} could start", candidate.Id);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex,
                             "Reembed job {JobId} dispatch or execution failed",
                             candidate.Id);
        }
        finally
        {
            mScheduled.TryRemove(key, out _);
        }
    }

    private async Task<JobRecord?> ClaimQueuedJobAsync(JobRecord candidate, IJobRepository jobRepo)
    {
        string executionClaimId = $"reembed-{Guid.NewGuid():N}";
        JobRecord? result;
        try
        {
            result = await jobRepo.TryClaimQueuedAsync(candidate.Id,
                                                       JobType.Reembed,
                                                       candidate.Profile,
                                                       executionClaimId,
                                                       DateTime.UtcNow,
                                                       mAppStoppingToken);
        }
        catch(Exception claimFailure)
        {
            JobRecord? current;
            try
            {
                current = await jobRepo.GetAsync(candidate.Id, CancellationToken.None);
            }
            catch(Exception confirmationFailure)
            {
                throw new AggregateException(
                    $"Reembed job '{candidate.Id}' claim failed and its durable outcome could not be confirmed.",
                    claimFailure,
                    confirmationFailure);
            }

            if (ClaimBelongsTo(current, candidate, executionClaimId))
                result = current;
            else
            {
                if (current == null || QueuedCandidateMatches(current, candidate))
                    throw;
                result = null;
            }
        }
        return result;
    }

    private async Task RunClaimedJobAsync(JobRecord jobRecord, IJobRepository jobRepo)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(mAppStoppingToken);
        mCancellationRegistry.Register(jobRecord.Id, cts);

        mBroadcaster.RecordJobStarted(jobRecord.Id,
                                      jobRecord.LibraryId ?? string.Empty,
                                      jobRecord.Version ?? string.Empty,
                                      string.Empty
                                     );

        mLogger.LogInformation("Running reembed job {JobId} for {LibraryId} v{Version}",
                               jobRecord.Id,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        try
        {
            await ExecuteReembedAsync(jobRecord, jobRepo, cts.Token);
        }
        catch(OperationCanceledException)
        {
            await MarkCancelledAsync(jobRecord, jobRepo);
        }
        catch(Exception ex)
        {
            await MarkFailedAsync(jobRecord, jobRepo, ex);
        }
        finally
        {
            mCancellationRegistry.Unregister(jobRecord.Id);
        }
    }

    private static bool ClaimBelongsTo(JobRecord? current,
                                       JobRecord candidate,
                                       string executionClaimId) =>
        current != null &&
        current.JobType == JobType.Reembed &&
        current.Status == JobStatus.Running &&
        string.Equals(current.Id, candidate.Id, StringComparison.Ordinal) &&
        string.Equals(current.Profile, candidate.Profile, StringComparison.Ordinal) &&
        string.Equals(current.ExecutionClaimId, executionClaimId, StringComparison.Ordinal);

    private static bool QueuedCandidateMatches(JobRecord? current, JobRecord candidate) =>
        current != null &&
        current.JobType == JobType.Reembed &&
        current.Status == JobStatus.Queued &&
        string.Equals(current.Id, candidate.Id, StringComparison.Ordinal) &&
        string.Equals(current.Profile, candidate.Profile, StringComparison.Ordinal);

    private static void ValidateQueuedReembed(JobRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        ArgumentException.ThrowIfNullOrEmpty(record.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(record.Version);
        if (record.JobType != JobType.Reembed || record.Status != JobStatus.Queued)
            throw new ArgumentException("Only durable queued reembed jobs can be dispatched.", nameof(record));
    }

    private async Task ExecuteReembedAsync(JobRecord jobRecord, IJobRepository jobRepo, CancellationToken ct)
    {
        var chunkRepo = mRepositoryFactory.GetChunkRepository(jobRecord.Profile);
        var libraryRepo = mRepositoryFactory.GetLibraryRepository(jobRecord.Profile);
        var options = JsonSerializer.Deserialize<ReembedOptions>(jobRecord.InputJson ?? string.Empty) ?? new ReembedOptions();

        var result = await mService.ReembedAsync(jobRecord.Profile,
                                                 chunkRepo,
                                                 libraryRepo,
                                                 jobRecord.LibraryId ?? string.Empty,
                                                 jobRecord.Version ?? string.Empty,
                                                 options,
                                                 (processed, total) => ProgressTick(jobRecord, jobRepo, processed, total),
                                                 ct
                                                );

        jobRecord.Status = JobStatus.Completed;
        jobRecord.PipelineState = nameof(JobStatus.Completed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        jobRecord.ItemsProcessed = result.Processed;
        jobRecord.ItemsTotal = result.Processed;
        jobRecord.ResultJson = JsonSerializer.Serialize(result);
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCompleted(jobRecord.Id, indexedPageCount: 0);

        mLogger.LogInformation("Reembed job {JobId} completed: processed={Processed}, provider={Provider}, model={Model}",
                               jobRecord.Id,
                               result.Processed,
                               result.EmbeddingProviderId,
                               result.EmbeddingModelName
                              );
    }

    private void ProgressTick(JobRecord jobRecord, IJobRepository jobRepo, int processed, int total)
    {
        jobRecord.ItemsProcessed = processed;
        jobRecord.ItemsTotal = total;
        jobRecord.LastProgressAt = DateTime.UtcNow;
        jobRepo.UpsertAsync(jobRecord).GetAwaiter().GetResult();
        mBroadcaster.RecordJobProgress(jobRecord.Id, processed, total, ProgressLabel);
    }

    private async Task MarkCancelledAsync(JobRecord jobRecord, IJobRepository jobRepo)
    {
        mLogger.LogInformation("Reembed job {JobId} was cancelled", jobRecord.Id);

        jobRecord.Status = JobStatus.Cancelled;
        jobRecord.PipelineState = nameof(JobStatus.Cancelled);
        jobRecord.CancelledAt = DateTime.UtcNow;
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCancelled(jobRecord.Id);
    }

    private async Task MarkFailedAsync(JobRecord jobRecord, IJobRepository jobRepo, Exception ex)
    {
        mLogger.LogError(ex, "Reembed job {JobId} failed", jobRecord.Id);

        jobRecord.Status = JobStatus.Failed;
        jobRecord.ErrorMessage = ex.Message;
        jobRecord.PipelineState = nameof(JobStatus.Failed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobFailed(jobRecord.Id, ex.Message);
    }

    private const string ProgressLabel = "chunks";

    private sealed record JobDispatchKey(string? Profile, string JobId);
}
