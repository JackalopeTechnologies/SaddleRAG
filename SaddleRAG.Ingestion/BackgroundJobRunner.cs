// BackgroundJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Runs generic background jobs in a fire-and-forget Task, tracking
///     lifecycle status in the unified <c>jobs</c> MongoDB collection so
///     callers can poll <c>get_job_status</c> without blocking the MCP
///     transport connection. Each job is one-shot; no concurrency
///     semaphore is needed.
///     <para>
///         The caller-facing input type is still
///         <see cref="BackgroundJobRecord" /> (with its snake_case
///         <c>JobType</c> string and generic <c>ItemsProcessed</c> /
///         <c>ItemsTotal</c> / <c>ItemsLabel</c> triple) so consumer
///         MCP tools don't change. The runner converts to
///         <see cref="JobRecord" /> at every persistence point and
///         writes only to the unified collection.
///     </para>
/// </summary>
public class BackgroundJobRunner : IBackgroundJobRunner
{
    public BackgroundJobRunner(RepositoryFactory repositoryFactory,
                               IMonitorBroadcaster broadcaster,
                               IJobCancellationRegistry cancellationRegistry,
                               IHostApplicationLifetime lifetime,
                               ILogger<BackgroundJobRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(cancellationRegistry);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        mRepositoryFactory = repositoryFactory;
        mBroadcaster = broadcaster;
        mCancellationRegistry = cancellationRegistry;
        mAppStoppingToken = lifetime.ApplicationStopping;
        mLogger = logger;
    }

    private readonly CancellationToken mAppStoppingToken;
    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IJobCancellationRegistry mCancellationRegistry;
    private readonly ILogger<BackgroundJobRunner> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;

    /// <summary>
    ///     Persist the job record, kick off background execution, and return the
    ///     job id immediately. The caller supplies the <paramref name="execute" />
    ///     delegate; the runner handles status transitions and error capture.
    /// </summary>
    public async Task<string> QueueAsync(BackgroundJobRecord jobRecord,
                                         Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute,
                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobRecord);
        ArgumentNullException.ThrowIfNull(execute);

        var jobRepo = mRepositoryFactory.GetJobRepository(jobRecord.Profile);
        await jobRepo.UpsertAsync(Project(jobRecord), ct);

        // Fire-and-forget background execution. Errors land in the job record.
        _ = Task.Run(() => RunJobAsync(jobRecord, execute), mAppStoppingToken);

        return jobRecord.Id;
    }

    private async Task RunJobAsync(BackgroundJobRecord jobRecord,
                                   Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute)
    {
        var jobRepo = mRepositoryFactory.GetJobRepository(jobRecord.Profile);

        jobRecord.Status = ScrapeJobStatus.Running;
        jobRecord.StartedAt = DateTime.UtcNow;
        jobRecord.PipelineState = PipelineStateRunning;
        await jobRepo.UpsertAsync(Project(jobRecord));

        mBroadcaster.RecordJobStarted(jobRecord.Id,
                                      jobRecord.LibraryId ?? string.Empty,
                                      jobRecord.Version ?? string.Empty,
                                      string.Empty
                                     );

        mLogger.LogInformation("Running background job {JobId} ({JobType}) for {LibraryId}/{Version}",
                               jobRecord.Id,
                               jobRecord.JobType,
                               jobRecord.LibraryId,
                               jobRecord.Version
                              );

        Action<int, int> onProgress = (processed, total) => ProgressTick(jobRecord, jobRepo, processed, total);

        var jobType = LegacyJobTypeToEnum(jobRecord.JobType);
        CancellationTokenSource? cts = null;
        CancellationToken executeToken;
        if (jobType.IsCancellable())
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(mAppStoppingToken);
            mCancellationRegistry.Register(jobRecord.Id, cts);
            executeToken = cts.Token;
        }
        else
            executeToken = mAppStoppingToken;

        try
        {
            await execute(jobRecord, onProgress, executeToken);
            await MarkCompletedAsync(jobRecord, jobRepo);
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
            if (cts != null)
            {
                mCancellationRegistry.Unregister(jobRecord.Id);
                cts.Dispose();
            }
        }
    }

    private void ProgressTick(BackgroundJobRecord jobRecord, IJobRepository jobRepo, int processed, int total)
    {
        jobRecord.ItemsProcessed = processed;
        jobRecord.ItemsTotal = total;
        jobRecord.LastProgressAt = DateTime.UtcNow;
        jobRepo.UpsertAsync(Project(jobRecord)).GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(jobRecord.ItemsLabel))
            mBroadcaster.RecordJobProgress(jobRecord.Id, processed, total, jobRecord.ItemsLabel);
    }

    private async Task MarkCompletedAsync(BackgroundJobRecord jobRecord, IJobRepository jobRepo)
    {
        jobRecord.Status = ScrapeJobStatus.Completed;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(Project(jobRecord));

        mBroadcaster.RecordJobCompleted(jobRecord.Id, indexedPageCount: 0);

        mLogger.LogInformation("Background job {JobId} ({JobType}) completed",
                               jobRecord.Id,
                               jobRecord.JobType
                              );
    }

    private async Task MarkCancelledAsync(BackgroundJobRecord jobRecord, IJobRepository jobRepo)
    {
        mLogger.LogInformation("Background job {JobId} ({JobType}) was cancelled",
                               jobRecord.Id,
                               jobRecord.JobType
                              );

        jobRecord.Status = ScrapeJobStatus.Cancelled;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
        jobRecord.CancelledAt = DateTime.UtcNow;
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(Project(jobRecord));

        mBroadcaster.RecordJobCancelled(jobRecord.Id);
    }

    private async Task MarkFailedAsync(BackgroundJobRecord jobRecord, IJobRepository jobRepo, Exception ex)
    {
        mLogger.LogError(ex, "Background job {JobId} ({JobType}) failed", jobRecord.Id, jobRecord.JobType);

        jobRecord.Status = ScrapeJobStatus.Failed;
        jobRecord.ErrorMessage = ex.Message;
        jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
        jobRecord.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(Project(jobRecord));

        mBroadcaster.RecordJobFailed(jobRecord.Id, ex.Message);
    }

    private static JobRecord Project(BackgroundJobRecord source) => new()
    {
        Id              = source.Id,
        JobType         = LegacyJobTypeToEnum(source.JobType),
        Profile         = source.Profile,
        LibraryId       = source.LibraryId,
        Version         = source.Version,
        InputJson       = source.InputJson,
        Status          = (JobStatus) (int) source.Status,
        PipelineState   = source.PipelineState,
        ItemsProcessed  = source.ItemsProcessed,
        ItemsTotal      = source.ItemsTotal,
        ItemsLabel      = source.ItemsLabel,
        ResultJson      = source.ResultJson,
        ErrorMessage    = source.ErrorMessage,
        CreatedAt       = source.CreatedAt,
        StartedAt       = source.StartedAt,
        CompletedAt     = source.CompletedAt,
        LastProgressAt  = source.LastProgressAt,
        CancelledAt     = source.CancelledAt
    };

    private static JobType LegacyJobTypeToEnum(string legacyType) => legacyType switch
    {
        "dryrun_scrape"              => JobType.DryRunScrape,
        "rechunk"                    => JobType.Rechunk,
        "rename_library"             => JobType.RenameLibrary,
        "delete_version"             => JobType.DeleteVersion,
        "delete_library"             => JobType.DeleteLibrary,
        "index_project_dependencies" => JobType.IndexProjectDependencies,
        "submit_url_correction"      => JobType.SubmitUrlCorrection,
        "cleanup_audit_log"          => JobType.CleanupAuditLog,
        "cleanup_jobs"               => JobType.CleanupJobs,
        "cleanup_orphans"            => JobType.CleanupOrphans,
        var _                        => JobType.Unknown
    };

    private const string PipelineStateRunning = "Running";
}
