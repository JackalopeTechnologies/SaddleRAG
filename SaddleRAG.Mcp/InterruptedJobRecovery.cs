using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;

namespace SaddleRAG.Mcp;

/// <summary>Stops affected jobs and persists their interrupted status after reconnection, without replaying work.</summary>
public sealed class InterruptedJobRecovery(JobCancellationRegistry registry, SaddleRagDbContextFactory contexts,
                                           RepositoryFactory repositories, IMonitorBroadcaster broadcaster,
                                           ILogger<InterruptedJobRecovery> logger)
{
    private const string InterruptedMessage =
        "MongoDB became unavailable during this job. Work was interrupted; review partial results before starting a new job.";

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        foreach (var job in registry.GetProfiles())
        {
            if (contexts.GetForProfile(job.Value).Reachability.HealthCheckFailed)
            {
                registry.RecordInterrupted(job.Key, job.Value, InterruptedMessage);
                broadcaster.RecordJobFailed(job.Key, InterruptedMessage);
                await registry.TryCancelAsync(job.Key);
            }
        }

        foreach (var job in registry.GetInterrupted())
        {
            broadcaster.RecordJobFailed(job.JobId, job.Detail);
            if (!registry.IsRegistered(job.JobId)
                && !contexts.GetForProfile(job.Profile).Reachability.HealthCheckFailed)
            {
                try
                {
                    var repository = repositories.GetJobRepository(job.Profile);
                    await SaveInterruptedAsync(repository, job, cancellationToken);
                    registry.AcknowledgeInterrupted(job.JobId);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Could not save interrupted status for job {JobId}; will retry", job.JobId);
                }
            }
        }
    }

    private static async Task SaveInterruptedAsync(IJobRepository repository, InterruptedJob job,
                                                    CancellationToken cancellationToken)
    {
        JobRecord? record = await repository.GetAsync(job.JobId, cancellationToken);
        if (record is not null)
        {
            record.Status = JobStatus.Failed;
            record.ErrorMessage = job.Detail;
            record.PipelineState = nameof(JobStatus.Failed);
            record.CompletedAt = DateTime.UtcNow;
            await repository.UpsertAsync(record, cancellationToken);
        }
    }
}
