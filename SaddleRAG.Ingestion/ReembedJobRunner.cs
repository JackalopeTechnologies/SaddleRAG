// ReembedJobRunner.cs
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
///     Runs reembed jobs in the background, tracking status in the
///     unified <c>jobs</c> MongoDB collection so the caller can poll
///     <c>get_reembed_status</c> without blocking the MCP transport
///     connection. Writes typed <see cref="ReembedOptions" /> and
///     <see cref="ReembedResult" /> blobs into
///     <see cref="JobRecord.InputJson" /> and
///     <see cref="JobRecord.ResultJson" />; consumer code reads via the
///     wrappers in <see cref="ReembedJobPayloads" />.
/// </summary>
public class ReembedJobRunner
{
    public ReembedJobRunner(ReembedService service,
                            RepositoryFactory repositoryFactory,
                            IMonitorBroadcaster broadcaster,
                            IHostApplicationLifetime lifetime,
                            ILogger<ReembedJobRunner> logger)
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
    private readonly ILogger<ReembedJobRunner> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly ReembedService mService;

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

        // Fire-and-forget background execution. Errors land in the job record.
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

        mLogger.LogInformation("Running reembed job {JobId} for {LibraryId} v{Version}",
                               jobRecord.Id,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        try
        {
            await ExecuteReembedAsync(jobRecord, jobRepo);
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

    private async Task ExecuteReembedAsync(JobRecord jobRecord, IJobRepository jobRepo)
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

    private const string PipelineStateRunning = "Running";
    private const string ProgressLabel = "chunks";
}
