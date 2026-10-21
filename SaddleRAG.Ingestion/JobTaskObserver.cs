using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;

namespace SaddleRAG.Ingestion;

/// <summary>Observes background task failures even when saving the terminal status also fails.</summary>
internal static class JobTaskObserver
{
    internal const string FailureDetail =
        "The job stopped before its final status could be saved. Review the logs and partial results before starting a new job.";

    internal static async Task RunAsync(Func<Task> execute, string jobId, string? profile,
                                        IJobCancellationRegistry registry, IMonitorBroadcaster? broadcaster,
                                        ILogger logger)
    {
        try
        {
            await execute();
        }
        catch (Exception exception)
        {
            broadcaster?.RecordJobFailed(jobId, FailureDetail);
            logger.LogError(exception, "Job {JobId} stopped without a saved final status", jobId);
            try
            {
                registry.RecordInterrupted(jobId, profile, FailureDetail);
            }
            catch (Exception recoveryException)
            {
                logger.LogError(recoveryException, "Could not retain interrupted status for job {JobId}", jobId);
            }
        }
    }
}
