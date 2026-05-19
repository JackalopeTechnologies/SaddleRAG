// ScrapeJobRunner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Collections.Concurrent;
using System.Text.Json;
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
///     Runs scrape jobs in the background, tracks status in the unified
///     <c>jobs</c> MongoDB collection, and reloads the per-profile
///     vector index when ingestion completes.
///     <para>
///         Internally still uses <see cref="ScrapeJobRecord" /> as the
///         in-memory domain object so the <see cref="IngestionOrchestrator" />
///         callback contract is unchanged; every persistence call
///         converts to <see cref="JobRecord" /> via
///         <see cref="ProjectToUnified" /> and writes only to the
///         unified collection.
///     </para>
/// </summary>
public class ScrapeJobRunner : IScrapeJobQueue
{
    public ScrapeJobRunner(IngestionOrchestrator orchestrator,
                           IChunkRepository chunkRepository,
                           IVectorSearchProvider vectorSearch,
                           ILibraryRepository libraryRepository,
                           ILogger<ScrapeJobRunner> logger,
                           RepositoryFactory repositoryFactory,
                           IJobCancellationRegistry cancellationRegistry,
                           IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(cancellationRegistry);
        mOrchestrator = orchestrator;
        mChunkRepository = chunkRepository;
        mVectorSearch = vectorSearch;
        mLibraryRepository = libraryRepository;
        mLogger = logger;
        mRepositoryFactory = repositoryFactory;
        mCancellationRegistry = cancellationRegistry;
        mAppStoppingToken = lifetime.ApplicationStopping;
    }

    private readonly CancellationToken mAppStoppingToken;
    private readonly IJobCancellationRegistry mCancellationRegistry;
    private readonly IChunkRepository mChunkRepository;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly ILogger<ScrapeJobRunner> mLogger;
    private readonly IngestionOrchestrator mOrchestrator;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly IVectorSearchProvider mVectorSearch;

    /// <summary>
    ///     Queue a job and kick off background execution.
    ///     Returns the job id immediately.
    /// </summary>
    public virtual async Task<string> QueueAsync(ScrapeJob job, string? profile = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var jobRecord = new ScrapeJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                Job = job,
                                Profile = profile,
                                Status = ScrapeJobStatus.Queued
                            };

        var jobRepo = mRepositoryFactory.GetJobRepository(profile);
        await jobRepo.UpsertAsync(ProjectToUnified(jobRecord), ct);

        // Fire-and-forget background execution. Errors land in the job record.
        _ = Task.Run(() => RunJobAsync(jobRecord), mAppStoppingToken);

        return jobRecord.Id;
    }

    private async Task RunJobAsync(ScrapeJobRecord jobRecord)
    {
        var lockKey = $"{jobRecord.Job.LibraryId}/{jobRecord.Job.Version}";
        var semaphore = smJobLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

        await semaphore.WaitAsync(mAppStoppingToken);
        CancellationTokenSource? cts = null;
        var jobRepo = mRepositoryFactory.GetJobRepository(jobRecord.Profile);
        try
        {
            jobRecord.Status = ScrapeJobStatus.Running;
            jobRecord.StartedAt = DateTime.UtcNow;
            jobRecord.PipelineState = PipelineStateStarting;
            await jobRepo.UpsertAsync(ProjectToUnified(jobRecord));

            mLogger.LogInformation("Running scrape job {JobId} for {LibraryId} v{Version}",
                                   jobRecord.Id,
                                   jobRecord.Job.LibraryId,
                                   jobRecord.Job.Version
                                  );

            cts = CancellationTokenSource.CreateLinkedTokenSource(mAppStoppingToken);
            mCancellationRegistry.Register(jobRecord.Id, cts);

            await mOrchestrator.IngestAsync(jobRecord.Job,
                                            jobRecord.Profile,
                                            jobRecord.Job.ForceClean,
                                            updatedRecord => OnProgressTick(jobRecord, jobRepo, updatedRecord),
                                            jobRecord,
                                            cts.Token
                                           );

            // Reload the vector index for this library version so the new
            // chunks are immediately searchable via search_docs.
            await ReloadIndexForLibraryAsync(jobRecord.Profile, jobRecord.Job.LibraryId, jobRecord.Job.Version);

            jobRecord.Status = ScrapeJobStatus.Completed;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Completed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(ProjectToUnified(jobRecord));

            mLogger.LogInformation("Scrape job {JobId} completed successfully", jobRecord.Id);
        }
        catch(Exception) when(cts is { IsCancellationRequested: true })
        {
            mLogger.LogInformation("Scrape job {JobId} was cancelled", jobRecord.Id);

            if (jobRecord.Status != ScrapeJobStatus.Cancelled)
            {
                jobRecord.Status = ScrapeJobStatus.Cancelled;
                jobRecord.PipelineState = nameof(ScrapeJobStatus.Cancelled);
                jobRecord.CancelledAt = DateTime.UtcNow;
                jobRecord.CompletedAt = DateTime.UtcNow;
                await jobRepo.UpsertAsync(ProjectToUnified(jobRecord));
            }
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Scrape job {JobId} failed", jobRecord.Id);

            jobRecord.Status = ScrapeJobStatus.Failed;
            jobRecord.ErrorMessage = ex.Message;
            jobRecord.PipelineState = nameof(ScrapeJobStatus.Failed);
            jobRecord.CompletedAt = DateTime.UtcNow;
            await jobRepo.UpsertAsync(ProjectToUnified(jobRecord));
        }
        finally
        {
            if (cts != null)
            {
                mCancellationRegistry.Unregister(jobRecord.Id);
                cts.Dispose();
            }

            semaphore.Release();
        }
    }

    private static void OnProgressTick(ScrapeJobRecord jobRecord, IJobRepository jobRepo, ScrapeJobRecord updatedRecord)
    {
        bool counterIncreased =
            updatedRecord.PagesQueued != jobRecord.PagesQueued ||
            updatedRecord.PagesFetched != jobRecord.PagesFetched ||
            updatedRecord.PagesClassified != jobRecord.PagesClassified ||
            updatedRecord.ChunksGenerated != jobRecord.ChunksGenerated ||
            updatedRecord.ChunksEmbedded != jobRecord.ChunksEmbedded ||
            updatedRecord.ChunksCompleted != jobRecord.ChunksCompleted ||
            updatedRecord.PagesCompleted != jobRecord.PagesCompleted;

        jobRecord.PipelineState = updatedRecord.PipelineState;
        jobRecord.PagesQueued = updatedRecord.PagesQueued;
        jobRecord.PagesFetched = updatedRecord.PagesFetched;
        jobRecord.PagesClassified = updatedRecord.PagesClassified;
        jobRecord.ChunksGenerated = updatedRecord.ChunksGenerated;
        jobRecord.ChunksEmbedded = updatedRecord.ChunksEmbedded;
        jobRecord.ChunksCompleted = updatedRecord.ChunksCompleted;
        jobRecord.PagesCompleted = updatedRecord.PagesCompleted;
        jobRecord.ErrorCount = updatedRecord.ErrorCount;

        if (counterIncreased)
            jobRecord.LastProgressAt = DateTime.UtcNow;

        jobRepo.UpsertAsync(ProjectToUnified(jobRecord)).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Rebuild the in-memory vector index for a library version
    ///     from the chunks currently stored in MongoDB.
    /// </summary>
    public async Task ReloadIndexForLibraryAsync(string? profile,
                                                 string libraryId,
                                                 string version,
                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var chunks = await mChunkRepository.GetChunksAsync(libraryId, version, ct);
        var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

        await mVectorSearch.IndexChunksAsync(profile, libraryId, version, embeddedChunks, ct);

        mLogger.LogInformation("Reloaded vector index for {Profile}/{Library} v{Version}: {Count} chunks",
                               profile ?? "(default)",
                               libraryId,
                               version,
                               embeddedChunks.Count
                              );
    }

    /// <summary>
    ///     Reload all library indices for a given profile.
    /// </summary>
    public async Task ReloadProfileAsync(string? profile, CancellationToken ct = default)
    {
        var libraryRepo = mRepositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = mRepositoryFactory.GetChunkRepository(profile);

        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        foreach(var lib in libraries)
        {
            var chunks = await chunkRepo.GetChunksAsync(lib.Id, lib.CurrentVersion, ct);
            var embeddedChunks = chunks.Where(c => c.Embedding != null).ToList();

            await mVectorSearch.IndexChunksAsync(profile, lib.Id, lib.CurrentVersion, embeddedChunks, ct);
        }

        mLogger.LogInformation("Reloaded all libraries for profile {Profile} ({Count} libraries)",
                               profile ?? "(default)",
                               libraries.Count
                              );
    }

    private static JobRecord ProjectToUnified(ScrapeJobRecord source) => new()
    {
        Id              = source.Id,
        JobType         = JobType.Scrape,
        Profile         = source.Profile,
        LibraryId       = source.Job.LibraryId,
        Version         = source.Job.Version,
        InputJson       = JsonSerializer.Serialize(source.Job),
        Status          = (JobStatus) (int) source.Status,
        PipelineState   = source.PipelineState,
        ItemsProcessed  = source.PagesCompleted,
        ItemsTotal      = 0,
        ItemsLabel      = ItemsLabelPages,
        ScrapeProgress  = new ScrapeProgress
        {
            PagesQueued     = source.PagesQueued,
            PagesFetched    = source.PagesFetched,
            PagesClassified = source.PagesClassified,
            ChunksGenerated = source.ChunksGenerated,
            ChunksEmbedded  = source.ChunksEmbedded,
            ChunksCompleted = source.ChunksCompleted,
            PagesCompleted  = source.PagesCompleted
        },
        ErrorCount      = source.ErrorCount,
        ErrorMessage    = source.ErrorMessage,
        CreatedAt       = source.CreatedAt,
        StartedAt       = source.StartedAt,
        CompletedAt     = source.CompletedAt,
        LastProgressAt  = source.LastProgressAt,
        CancelledAt     = source.CancelledAt
    };

    private const string PipelineStateStarting = "Starting";
    private const string ItemsLabelPages = "pages";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> smJobLocks =
        new ConcurrentDictionary<string, SemaphoreSlim>();
}
