// RescrubJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Runs rescrub jobs in the background, tracking status in MongoDB so
///     the caller can poll get_rescrub_status without blocking the MCP
///     transport connection.
/// </summary>
public class RescrubJobRunner
{
    public RescrubJobRunner(RescrubService service,
                            RepositoryFactory repositoryFactory,
                            IHostApplicationLifetime lifetime,
                            ILogger<RescrubJobRunner> logger)
    {
        mService = service;
        mRepositoryFactory = repositoryFactory;
        mAppStoppingToken = lifetime.ApplicationStopping;
        mLogger = logger;
    }

    private readonly CancellationToken mAppStoppingToken;
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

        var jobRepo = mRepositoryFactory.GetRescrubJobRepository(profile);
        var jobRecord = new RescrubJobRecord
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

    private async Task RunJobAsync(RescrubJobRecord jobRecord)
    {
        var jobRepo = mRepositoryFactory.GetRescrubJobRepository(jobRecord.Profile);

        jobRecord.Status = ScrapeJobStatus.Running;
        jobRecord.StartedAt = DateTime.UtcNow;
        jobRecord.PipelineState = PipelineStateRunning;
        await jobRepo.UpsertAsync(jobRecord);

        mLogger.LogInformation("Running rescrub job {JobId} for {LibraryId} v{Version}",
                               jobRecord.Id,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        try
        {
            var chunkRepo = mRepositoryFactory.GetChunkRepository(jobRecord.Profile);
            var profileRepo = mRepositoryFactory.GetLibraryProfileRepository(jobRecord.Profile);
            var indexRepo = mRepositoryFactory.GetLibraryIndexRepository(jobRecord.Profile);
            var bm25ShardRepo = mRepositoryFactory.GetBm25ShardRepository(jobRecord.Profile);
            var excludedRepo = mRepositoryFactory.GetExcludedSymbolsRepository(jobRecord.Profile);
            var libraryRepo = mRepositoryFactory.GetLibraryRepository(jobRecord.Profile);

            var result = await mService.RescrubAsync(chunkRepo,
                                                     profileRepo,
                                                     indexRepo,
                                                     bm25ShardRepo,
                                                     excludedRepo,
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
                                                     },
                                                     mAppStoppingToken
                                                    );

            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            jobRecord.ChunksProcessed = result.Processed;
            jobRecord.ChunksChanged = result.Changed;
            jobRecord.ChunksTotal = result.Processed;
            jobRecord.Result = result;
            await jobRepo.UpsertAsync(jobRecord);

            mLogger.LogInformation("Rescrub job {JobId} completed: processed={Processed}, changed={Changed}",
                                   jobRecord.Id,
                                   result.Processed,
                                   result.Changed
                                  );
        }
        catch(OperationCanceledException)
        {
            mLogger.LogInformation("Rescrub job {JobId} was cancelled", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Cancelled;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
            jobRecord.CancelledAt = DateTime.UtcNow;
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Rescrub job {JobId} failed", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Failed;
            jobRecord.ErrorMessage = ex.Message;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);
        }
    }

    private const string PipelineStateRunning = "Running";
}
