// JobCancellationService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     End-to-end cancel flow shared by the <c>cancel_job</c> MCP tool,
///     the monitor API endpoint, and the URL-correction tool. Looks up
///     the job in the unified jobs collection, refuses jobs whose
///     <see cref="JobType" /> is not <see cref="JobTypeCapabilities.IsCancellable" />,
///     signals the registered <see cref="CancellationTokenSource" /> when
///     present, and writes the Cancelled status back to MongoDB.
/// </summary>
public class JobCancellationService
{
    public JobCancellationService(IJobCancellationRegistry registry,
                                  RepositoryFactory repositoryFactory,
                                  IMonitorBroadcaster broadcaster)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        mRegistry = registry;
        mRepositoryFactory = repositoryFactory;
        mBroadcaster = broadcaster;
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IJobCancellationRegistry mRegistry;
    private readonly RepositoryFactory mRepositoryFactory;

    /// <summary>
    ///     Attempt to cancel the job identified by <paramref name="jobId" />.
    ///     Reads the unified jobs collection for the supplied
    ///     <paramref name="profile" /> (default profile when null), classifies
    ///     the outcome, and persists the terminal state when cancellation is
    ///     actually performed.
    /// </summary>
    public virtual async Task<CancelScrapeOutcome> CancelAsync(string jobId,
                                                               string? profile = null,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = mRepositoryFactory.GetJobRepository(profile);
        var record = await jobRepo.GetAsync(jobId, ct);

        CancelScrapeOutcome result;
        switch(record)
        {
            case null:
                result = CancelScrapeOutcome.NotFound;
                break;
            case { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled }:
                result = CancelScrapeOutcome.AlreadyTerminal;
                break;
            case var r when !r.JobType.IsCancellable():
                result = CancelScrapeOutcome.NotCancellable;
                break;
            default:
                result = await SignalAndPersistAsync(jobRepo, record, jobId, ct);
                break;
        }

        return result;
    }

    private async Task<CancelScrapeOutcome> SignalAndPersistAsync(IJobRepository jobRepo,
                                                                  JobRecord record,
                                                                  string jobId,
                                                                  CancellationToken ct)
    {
        var signalled = await mRegistry.TryCancelAsync(jobId);

        record.Status = JobStatus.Cancelled;
        record.PipelineState = nameof(JobStatus.Cancelled);
        record.CancelledAt = DateTime.UtcNow;
        record.CompletedAt = DateTime.UtcNow;
        await jobRepo.UpsertAsync(record, ct);

        // Notify the in-process broadcaster so the SignalR landing-page
        // active-jobs list drops this job immediately, instead of waiting
        // for the pipeline drain (which can run ~10-15s post-cancel) to
        // hit its own RecordJobCancelled call. Without this, the UI shows
        // the job as Running with stale stats while the DB row already
        // says Cancelled -- the staleness the user reported. The runner's
        // catch block still fires RecordJobCancelled later; that second
        // call is a no-op because mJobs has already been cleared here.
        mBroadcaster.RecordJobCancelled(jobId);

        var result = signalled ? CancelScrapeOutcome.Signalled : CancelScrapeOutcome.OrphanCleanedUp;
        return result;
    }
}
