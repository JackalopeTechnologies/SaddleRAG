// QueuedReembedJobRecovery.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Rediscovers durable queued re-embed jobs after process startup and
///     dispatches them through the same claim path used by live requests.
/// </summary>
public sealed class QueuedReembedJobRecovery
{
    public QueuedReembedJobRecovery(SaddleRagDbContextFactory contextFactory,
                                    RepositoryFactory repositoryFactory,
                                    IReembedJobDispatcher dispatcher,
                                    ILogger<QueuedReembedJobRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        mContextFactory = contextFactory;
        mRepositoryFactory = repositoryFactory;
        mDispatcher = dispatcher;
        mLogger = logger;
    }

    private readonly SaddleRagDbContextFactory mContextFactory;
    private readonly IReembedJobDispatcher mDispatcher;
    private readonly ILogger<QueuedReembedJobRecovery> mLogger;
    private readonly RepositoryFactory mRepositoryFactory;

    /// <summary>
    ///     Schedules every recoverable queued re-embed job in the default and
    ///     configured profiles. A profile failure does not stop other profiles.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        var scheduled = 0;
        foreach(string? profile in GetProfileSelectors())
            scheduled += await RecoverProfileAsync(profile, ct);

        mLogger.LogInformation("Queued reembed recovery scheduled {Count} job(s)", scheduled);
        return scheduled;
    }

    private async Task<int> RecoverProfileAsync(string? profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<JobRecord> queued = [];
        bool readSucceeded = false;
        try
        {
            IJobRepository jobs = mRepositoryFactory.GetJobRepository(profile);
            queued = await jobs.ListQueuedAsync(JobType.Reembed, profile, ct);
            readSucceeded = true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex,
                               "Queued reembed recovery could not read profile {Profile}",
                               profile ?? DefaultProfileLabel);
        }

        var scheduled = 0;
        if (readSucceeded)
        {
            foreach(JobRecord job in queued)
                scheduled += TryRecoverJob(profile, job);
        }
        return scheduled;
    }

    private int TryRecoverJob(string? profile, JobRecord job)
    {
        var scheduled = 0;
        if (!IsRecoverable(profile, job))
        {
            mLogger.LogWarning(
                "Queued reembed recovery skipped job {JobId} because its durable profile or state did not match profile {Profile}",
                job.Id,
                profile ?? DefaultProfileLabel);
        }
        else
        {
            try
            {
                if (mDispatcher.TryDispatchPersisted(job))
                    scheduled = 1;
            }
            catch(Exception ex)
            {
                mLogger.LogWarning(ex,
                                   "Queued reembed recovery could not dispatch job {JobId} for profile {Profile}",
                                   job.Id,
                                   profile ?? DefaultProfileLabel);
            }
        }
        return scheduled;
    }

    private IReadOnlyList<string?> GetProfileSelectors()
    {
        var result = new List<string?> { null };
        foreach(string profile in mContextFactory.GetProfileNames())
        {
            if (!result.Contains(profile, StringComparer.Ordinal))
                result.Add(profile);
        }
        return result;
    }

    private static bool IsRecoverable(string? profile, JobRecord job) =>
        job.JobType == JobType.Reembed &&
        job.Status == JobStatus.Queued &&
        string.Equals(job.Profile, profile, StringComparison.Ordinal);

    private const string DefaultProfileLabel = "(default)";
}
