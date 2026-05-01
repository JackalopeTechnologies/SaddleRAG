// BackgroundJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Runs generic background jobs in a fire-and-forget Task, tracking lifecycle
///     status in MongoDB so callers can poll get_job_status without blocking the
///     MCP transport connection. Each job is one-shot; no concurrency semaphore
///     is needed.
/// </summary>
public class BackgroundJobRunner : IBackgroundJobRunner
{
    public BackgroundJobRunner(RepositoryFactory repositoryFactory,
                               IHostApplicationLifetime lifetime,
                               ILogger<BackgroundJobRunner> logger)
    {
        mRepositoryFactory = repositoryFactory;
        mAppStoppingToken = lifetime.ApplicationStopping;
        mLogger = logger;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly CancellationToken mAppStoppingToken;
    private readonly ILogger<BackgroundJobRunner> mLogger;

    /// <summary>
    ///     Persist the job record, kick off background execution, and return the
    ///     job id immediately. The caller supplies the <paramref name="execute"/>
    ///     delegate; the runner handles status transitions and error capture.
    /// </summary>
    /// <param name="jobRecord">
    ///     Pre-built record with <c>Status = Queued</c>. Must have a unique
    ///     <c>Id</c> (GUID string).
    /// </param>
    /// <param name="execute">
    ///     The operation to run. Receives the job record, an optional progress
    ///     callback <c>(processed, total) =&gt; void</c>, and a cancellation
    ///     token. May be null for the progress callback when the operation does
    ///     not report incremental progress.
    /// </param>
    /// <param name="ct">Caller cancellation token used only for the initial upsert.</param>
    /// <returns>The job id from <paramref name="jobRecord"/>.</returns>
    public async Task<string> QueueAsync(
        BackgroundJobRecord jobRecord,
        Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobRecord);
        ArgumentNullException.ThrowIfNull(execute);

        var jobRepo = mRepositoryFactory.GetBackgroundJobRepository(jobRecord.Profile);
        await jobRepo.UpsertAsync(jobRecord, ct);

        // Fire-and-forget background execution. Errors land in the job record.
        _ = Task.Run(() => RunJobAsync(jobRecord, execute), mAppStoppingToken);

        return jobRecord.Id;
    }

    private async Task RunJobAsync(
        BackgroundJobRecord jobRecord,
        Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> execute)
    {
        var jobRepo = mRepositoryFactory.GetBackgroundJobRepository(jobRecord.Profile);

        jobRecord.Status = ScrapeJobStatus.Running;
        jobRecord.StartedAt = DateTime.UtcNow;
        jobRecord.PipelineState = PipelineStateRunning;
        await jobRepo.UpsertAsync(jobRecord);

        mLogger.LogInformation(
            "Running background job {JobId} ({JobType}) for {LibraryId}/{Version}",
            jobRecord.Id,
            jobRecord.JobType,
            jobRecord.LibraryId,
            jobRecord.Version
        );

        Action<int, int> onProgress = (processed, total) =>
        {
            jobRecord.ItemsProcessed = processed;
            jobRecord.ItemsTotal = total;
            jobRecord.LastProgressAt = DateTime.UtcNow;
            jobRepo.UpsertAsync(jobRecord).GetAwaiter().GetResult();
        };

        try
        {
            await execute(jobRecord, onProgress, mAppStoppingToken);

            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);

            mLogger.LogInformation(
                "Background job {JobId} ({JobType}) completed",
                jobRecord.Id,
                jobRecord.JobType
            );
        }
        catch (OperationCanceledException)
        {
            mLogger.LogInformation(
                "Background job {JobId} ({JobType}) was cancelled",
                jobRecord.Id,
                jobRecord.JobType
            );

            jobRecord.Status = ScrapeJobStatus.Cancelled;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
            jobRecord.CancelledAt = DateTime.UtcNow;
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);
        }
        catch (Exception ex)
        {
            mLogger.LogError(ex, "Background job {JobId} ({JobType}) failed", jobRecord.Id, jobRecord.JobType);

            jobRecord.Status = ScrapeJobStatus.Failed;
            jobRecord.ErrorMessage = ex.Message;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(jobRecord);
        }
    }

    private const string PipelineStateRunning = "Running";
}
