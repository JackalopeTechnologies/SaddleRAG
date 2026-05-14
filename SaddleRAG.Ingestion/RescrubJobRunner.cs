// RescrubJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

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
///     Runs reextract jobs in the background, tracking status in the
///     unified <c>jobs</c> MongoDB collection so the caller can poll
///     <c>get_reextract_status</c> without blocking the MCP transport
///     connection. Writes typed <see cref="RescrubOptions" /> and
///     <see cref="RescrubResult" /> blobs into
///     <see cref="JobRecord.InputJson" /> and
///     <see cref="JobRecord.ResultJson" />.
/// </summary>
public class RescrubJobRunner
{
    public RescrubJobRunner(RescrubService service,
                            RepositoryFactory repositoryFactory,
                            IMonitorBroadcaster broadcaster,
                            IHostApplicationLifetime lifetime,
                            ILogger<RescrubJobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        mService = service;
        mRepositoryFactory = repositoryFactory;
        mBroadcaster = broadcaster;
        mAppStoppingToken = lifetime.ApplicationStopping;
        mLogger = logger;
    }

    private readonly CancellationToken mAppStoppingToken;
    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly ILogger<RescrubJobRunner> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly RescrubService mService;

    /// <summary>
    ///     Queue a rescrub job and kick off background execution.
    ///     Returns the job id immediately.
    /// </summary>
    public async Task<string> QueueAsync(string libraryId,
                                         string version,
                                         RescrubOptions options,
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
                                JobType = JobType.Rescrub,
                                Profile = profile,
                                LibraryId = libraryId,
                                Version = version,
                                InputJson = JsonSerializer.Serialize(options),
                                Status = JobStatus.Queued,
                                ItemsLabel = ProgressLabel
                            };

        await jobRepo.UpsertAsync(jobRecord, ct);
        _ = Task.Run(() => RunJobAsync(jobRecord), mAppStoppingToken);
        return jobRecord.Id;
    }

    private async Task RunJobAsync(JobRecord jobRecord)
    {
        var jobRepo = mRepositoryFactory.GetJobRepository(jobRecord.Profile);

        jobRecord.Status = JobStatus.Running;
        jobRecord.StartedAt = DateTime.UtcNow;
        jobRecord.PipelineState = PipelineStateRunning;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobStarted(jobRecord.Id,
                                      jobRecord.LibraryId ?? string.Empty,
                                      jobRecord.Version ?? string.Empty,
                                      string.Empty
                                     );

        mLogger.LogInformation("Running rescrub job {JobId} for {LibraryId} v{Version}",
                               jobRecord.Id,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        try
        {
            await ExecuteRescrubAsync(jobRecord, jobRepo);
        }
        catch(OperationCanceledException)
        {
            await MarkCancelledAsync(jobRecord, jobRepo);
        }
        catch(Exception ex)
        {
            await MarkFailedAsync(jobRecord, jobRepo, ex);
        }
    }

    private async Task ExecuteRescrubAsync(JobRecord jobRecord, IJobRepository jobRepo)
    {
        var chunkRepo = mRepositoryFactory.GetChunkRepository(jobRecord.Profile);
        var profileRepo = mRepositoryFactory.GetLibraryProfileRepository(jobRecord.Profile);
        var indexRepo = mRepositoryFactory.GetLibraryIndexRepository(jobRecord.Profile);
        var bm25ShardRepo = mRepositoryFactory.GetBm25ShardRepository(jobRecord.Profile);
        var excludedRepo = mRepositoryFactory.GetExcludedSymbolsRepository(jobRecord.Profile);
        var libraryRepo = mRepositoryFactory.GetLibraryRepository(jobRecord.Profile);
        var options = JsonSerializer.Deserialize<RescrubOptions>(jobRecord.InputJson ?? string.Empty) ?? new RescrubOptions();

        var result = await mService.RescrubAsync(chunkRepo,
                                                 profileRepo,
                                                 indexRepo,
                                                 bm25ShardRepo,
                                                 excludedRepo,
                                                 libraryRepo,
                                                 jobRecord.LibraryId ?? string.Empty,
                                                 jobRecord.Version ?? string.Empty,
                                                 options,
                                                 (processed, total) => ProgressTick(jobRecord, jobRepo, processed, total),
                                                 mAppStoppingToken
                                                );

        jobRecord.Status = JobStatus.Completed;
        jobRecord.PipelineState = nameof(JobStatus.Completed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        jobRecord.ItemsProcessed = result.Processed;
        jobRecord.ItemsTotal = result.Processed;
        jobRecord.ResultJson = JsonSerializer.Serialize(result);
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCompleted(jobRecord.Id, indexedPageCount: 0);

        mLogger.LogInformation("Rescrub job {JobId} completed: processed={Processed}, changed={Changed}",
                               jobRecord.Id,
                               result.Processed,
                               result.Changed
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
        mLogger.LogInformation("Rescrub job {JobId} was cancelled", jobRecord.Id);

        jobRecord.Status = JobStatus.Cancelled;
        jobRecord.PipelineState = nameof(JobStatus.Cancelled);
        jobRecord.CancelledAt = DateTime.UtcNow;
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobCancelled(jobRecord.Id);
    }

    private async Task MarkFailedAsync(JobRecord jobRecord, IJobRepository jobRepo, Exception ex)
    {
        mLogger.LogError(ex, "Rescrub job {JobId} failed", jobRecord.Id);

        jobRecord.Status = JobStatus.Failed;
        jobRecord.ErrorMessage = ex.Message;
        jobRecord.PipelineState = nameof(JobStatus.Failed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobFailed(jobRecord.Id, ex.Message);
    }

    private const string PipelineStateRunning = "Running";
    private const string ProgressLabel = "chunks";
}
