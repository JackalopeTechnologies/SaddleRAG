// ReembedJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Runs reembed jobs in the background, tracking status in MongoDB so
///     the caller can poll get_reembed_status without blocking the MCP
///     transport connection.
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

        var jobRepo = mRepositoryFactory.GetReembedJobRepository(profile);
        var jobRecord = new ReembedJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                LibraryId = libraryId,
                                Version = version,
                                Options = options,
                                Profile = profile,
                                Status = ScrapeJobStatus.Queued
                            };

        await jobRepo.UpsertAsync(jobRecord, ct);

        // Fire-and-forget background execution. Errors land in the job record.
        _ = Task.Run(() => RunJobAsync(jobRecord), mAppStoppingToken);

        return jobRecord.Id;
    }

    private async Task RunJobAsync(ReembedJobRecord jobRecord)
    {
        var jobRepo = mRepositoryFactory.GetReembedJobRepository(jobRecord.Profile);

        jobRecord.Status = ScrapeJobStatus.Running;
        jobRecord.StartedAt = DateTime.UtcNow;
        jobRecord.PipelineState = PipelineStateRunning;
        await jobRepo.UpsertAsync(jobRecord);

        mBroadcaster.RecordJobStarted(jobRecord.Id,
                                      jobRecord.LibraryId,
                                      jobRecord.Version,
                                      string.Empty
                                     );

        mLogger.LogInformation("Running reembed job {JobId} for {LibraryId} v{Version}",
                               jobRecord.Id,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        try
        {
            var chunkRepo = mRepositoryFactory.GetChunkRepository(jobRecord.Profile);
            var libraryRepo = mRepositoryFactory.GetLibraryRepository(jobRecord.Profile);

            var result = await mService.ReembedAsync(jobRecord.Profile,
                                                     chunkRepo,
                                                     libraryRepo,
                                                     jobRecord.LibraryId,
                                                     jobRecord.Version,
                                                     jobRecord.Options,
                                                     (processed, total) =>
                                                     {
                                                         jobRecord.ChunksProcessed = processed;
                                                         jobRecord.ChunksTotal = total;
                                                         jobRecord.LastProgressAt = DateTime.UtcNow;
                                                         jobRepo.UpsertAsync(jobRecord).GetAwaiter().GetResult();
                                                         mBroadcaster.RecordJobProgress(jobRecord.Id,
                                                                  processed,
                                                                  total,
                                                                  ProgressLabel
                                                             );
                                                     },
                                                     mAppStoppingToken
                                                    );

            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            jobRecord.ChunksProcessed = result.Processed;
            jobRecord.ChunksTotal = result.Processed;
            jobRecord.Result = result;
            await jobRepo.UpsertAsync(jobRecord);

            mBroadcaster.RecordJobCompleted(jobRecord.Id, indexedPageCount: 0);

            mLogger.LogInformation("Reembed job {JobId} completed: processed={Processed}, provider={Provider}, model={Model}",
                                   jobRecord.Id,
                                   result.Processed,
                                   result.EmbeddingProviderId,
                                   result.EmbeddingModelName
                                  );
        }
        catch(OperationCanceledException)
        {
            mLogger.LogInformation("Reembed job {JobId} was cancelled", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Cancelled;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
            jobRecord.CancelledAt = DateTime.UtcNow;
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);

            mBroadcaster.RecordJobCancelled(jobRecord.Id);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Reembed job {JobId} failed", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Failed;
            jobRecord.ErrorMessage = ex.Message;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);

            mBroadcaster.RecordJobFailed(jobRecord.Id, ex.Message);
        }
    }

    private const string PipelineStateRunning = "Running";
    private const string ProgressLabel = "chunks";
}
