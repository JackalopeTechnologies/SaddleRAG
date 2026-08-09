// IngestionOrchestrator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Ingestion.Suspect;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Orchestrates the streaming ingestion pipeline:
///     crawl â†’ classify â†’ chunk â†’ embed â†’ index.
///     Each stage runs as a single async consumer connected by bounded channels.
/// </summary>
public class IngestionOrchestrator
{
    public IngestionOrchestrator(IPageCrawler crawler,
                                 ILlmClassifier llmClassifier,
                                 CategoryAwareChunker chunker,
                                 IEmbeddingProvider embeddingProvider,
                                 IVectorSearchProvider vectorSearch,
                                 ILibraryRepository libraryRepository,
                                 IPageRepository pageRepository,
                                 IChunkRepository chunkRepository,
                                 ILibraryProfileRepository libraryProfileRepository,
                                 ILibraryIndexRepository libraryIndexRepository,
                                 IBm25ShardRepository bm25ShardRepository,
                                 SuspectDetector suspectDetector,
                                 IScrapeAuditWriter auditWriter,
                                 IMonitorBroadcaster broadcaster,
                                 ILogger<IngestionOrchestrator> logger,
                                 ISourceDocumentRepository sourceDocumentRepository,
                                 RepositoryFactory repositoryFactory,
                                 ILibraryIngestionModeLeaseManager modeLeaseManager)
    {
        ArgumentNullException.ThrowIfNull(sourceDocumentRepository);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        mCrawler = crawler;
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mBm25ShardRepository = bm25ShardRepository;
        mLibraryIndexRepository = libraryIndexRepository;
        mLibraryRepository = libraryRepository;
        mLibraryProfileRepository = libraryProfileRepository;
        mVectorSearch = vectorSearch;
        mLlmClassifier = llmClassifier;
        mSuspectDetector = suspectDetector;
        mSourceDocumentRepository = sourceDocumentRepository;
        mRepositoryFactory = repositoryFactory;
        mModeLeaseManager = modeLeaseManager;
        mBroadcaster = broadcaster;
        mLogger = logger;
        mPageProcessor = new IngestionPageProcessor(llmClassifier,
                                                    chunker,
                                                    embeddingProvider,
                                                    vectorSearch,
                                                    logger,
                                                    sharedLoggerCategory: true);
        mCrawlStage = new CrawlStage(crawler, logger);
        mClassifyStage = new ClassifyStage(mPageProcessor, pageRepository, broadcaster, logger);
        mChunkStage = new ChunkStage(mPageProcessor, broadcaster, logger);
        mEmbedStage = new EmbedStage(mPageProcessor, chunkRepository, broadcaster, logger);
        mIndexStage = new IndexStage(vectorSearch, auditWriter, broadcaster, logger, llmClassifier);
        mFinalizer = new IngestionFinalizer(chunkRepository,
                                            bm25ShardRepository,
                                            libraryIndexRepository,
                                            libraryRepository,
                                            vectorSearch,
                                            embeddingProvider,
                                            libraryProfileRepository,
                                            suspectDetector,
                                            llmClassifier,
                                            mPageProcessor,
                                            logger,
                                            sourceDocumentRepository
                                           );
    }

    private readonly IMonitorBroadcaster mBroadcaster;

    private readonly CategoryAwareChunker mChunker;
    private readonly IChunkRepository mChunkRepository;
    private readonly ChunkStage mChunkStage;
    private readonly ClassifyStage mClassifyStage;

    private readonly IPageCrawler mCrawler;
    private readonly CrawlStage mCrawlStage;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly EmbedStage mEmbedStage;
    private readonly IngestionFinalizer mFinalizer;
    private readonly IndexStage mIndexStage;
    private readonly ILogger<IngestionOrchestrator> mLogger;
    private readonly ILlmClassifier mLlmClassifier;
    private readonly IngestionPageProcessor mPageProcessor;
    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;
    private readonly IBm25ShardRepository mBm25ShardRepository;
    private readonly ILibraryIndexRepository mLibraryIndexRepository;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly ILibraryProfileRepository mLibraryProfileRepository;
    private readonly IPageRepository mPageRepository;
    private readonly ISourceDocumentRepository mSourceDocumentRepository;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly SuspectDetector mSuspectDetector;
    private readonly IVectorSearchProvider mVectorSearch;

    /// <summary>
    ///     Run the streaming ingestion pipeline for a scrape job.
    /// </summary>
    public async Task IngestAsync(ScrapeJob job,
                                  string? profile = null,
                                  bool forceClean = false,
                                  Action<ScrapeJobRecord>? onProgress = null,
                                  ScrapeJobRecord? jobRecord = null,
                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using ILibraryIngestionModeLease modeLease =
            await AcquireWebModeLeaseAsync(profile, job.LibraryId, ct);
        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(ct, modeLease.OwnershipLostToken);
        await CommitWebOwnershipAsync(profile, job.LibraryId, modeLease, operation.Token);
        ProfileRepositories repositories = ResolveRepositories(profile);
        await EnsurePublishedVersionDoesNotExistAsync(job, repositories.Libraries, operation.Token);
        await WriteBuildingVersionAsync(job, repositories.Libraries, operation.Token);
        var progress = jobRecord ??
                       new ScrapeJobRecord
                           {
                               Id = Guid.NewGuid().ToString(),
                               Job = job,
                               Profile = profile
                           };

        try
        {
            await IngestCandidateAsync(job,
                                       profile,
                                       forceClean,
                                       onProgress,
                                       progress,
                                       repositories,
                                       operation.Token);
        }
        catch(OperationCanceledException ex)
        {
            await MarkCandidateFailedPreservingOriginalAsync(job,
                                                             profile,
                                                             PublicationCancelledMessage,
                                                             ex,
                                                             modeLease,
                                                             repositories);
            throw;
        }
        catch(Exception ex)
        {
            await MarkCandidateFailedPreservingOriginalAsync(job,
                                                             profile,
                                                             ex.Message,
                                                             ex,
                                                             modeLease,
                                                             repositories);
            throw;
        }
    }

    private static async Task EnsurePublishedVersionDoesNotExistAsync(ScrapeJob job,
                                                                       ILibraryRepository libraries,
                                                                       CancellationToken ct)
    {
        var existing = await libraries.GetVersionAsync(job.LibraryId, job.Version, ct);
        if (existing?.PublicationState == VersionPublicationState.Published)
        {
            throw new InvalidOperationException(
                $"Library '{job.LibraryId}' version '{job.Version}' is already Published and cannot be overwritten. " +
                PublishedVersionReuseInstruction);
        }
    }

    private async Task IngestCandidateAsync(ScrapeJob job,
                                            string? profile,
                                            bool forceClean,
                                            Action<ScrapeJobRecord>? onProgress,
                                            ScrapeJobRecord progress,
                                            ProfileRepositories repositories,
                                            CancellationToken ct)
    {
        ScrapeJob operationJob = string.IsNullOrEmpty(profile) ? job : job with { DatabaseProfile = profile };
        mLogger.LogInformation("Starting streaming ingestion for {LibraryId} v{Version}", job.LibraryId, job.Version);

        // Build resume URL set from existing pages in DB. SeedFromStoredPages
        // flips this from "skip already-fetched URLs" to "use stored URLs as
        // extra crawl seeds and re-fetch every one of them."
        var existingPages = await repositories.Pages.GetPagesAsync(job.LibraryId, job.Version, ct);
        IReadOnlySet<string>? resumeUrls = null;
        var seedUrls = new List<string>();

        if (existingPages.Count > 0 && job.SeedFromStoredPages)
        {
            seedUrls.AddRange(existingPages.Select(p => p.Url));
            mLogger.LogInformation("Seed-from-stored-pages mode: {Count} stored URLs will be re-fetched",
                                   seedUrls.Count
                                  );
        }

        if (job.SeedUrls is { Count: > 0 })
        {
            // Caller-supplied extra seed URLs (e.g., the /api/MathNet.X/index.htm
            // hub on DocFX-generated sites whose home page does not link into
            // the API tree). Union with any stored-page seeds so a single
            // scrape can refresh prior content AND fan out from new hubs.
            var configuredSeeds = job.SeedUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            seedUrls.AddRange(configuredSeeds);
            mLogger.LogInformation("Caller-supplied seed URLs: {Count} added to crawl queue",
                                   configuredSeeds.Count
                                  );
        }

        if (existingPages.Count > 0 && !job.SeedFromStoredPages && !forceClean)
        {
            resumeUrls = existingPages.Select(p => p.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            mLogger.LogInformation("Resume mode: {Count} existing pages found", resumeUrls.Count);
        }

        IReadOnlyList<string>? effectiveSeedUrls = seedUrls.Count == 0 ? null : seedUrls;

        // On force re-scrape, clear existing chunks before pipeline starts
        if (forceClean)
        {
            await repositories.Chunks.DeleteChunksAsync(job.LibraryId, job.Version, ct);
            mLogger.LogInformation("Force clean: deleted existing chunks for {LibraryId} v{Version}",
                                   job.LibraryId,
                                   job.Version
                                  );
        }

        // Create bounded channels
        var crawlToClassify = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var classifyToChunk = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var chunkToEmbed = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );
        var embedToIndex = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );

        progress.PipelineState = nameof(ScrapeJobStatus.Running);

        var auditCtx = new AuditContext
                           {
                               JobId = progress.Id,
                               LibraryId = job.LibraryId,
                               Version = job.Version,
                               Profile = profile
                           };

        mBroadcaster.RecordJobStarted(progress.Id, job.LibraryId, job.Version, job.RootUrl ?? string.Empty);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Launch all five stages
        var crawlTask = mCrawlStage.RunAsync(operationJob,
                                             crawlToClassify.Writer,
                                             resumeUrls,
                                             effectiveSeedUrls,
                                             progress,
                                             onProgress,
                                             cts
                                            );
        var classifyTask = repositories.Classify.RunAsync(operationJob,
                                                   crawlToClassify.Reader,
                                                   classifyToChunk.Writer,
                                                   progress,
                                                   onProgress,
                                                   cts
                                                  );
        var chunkTask = mChunkStage.RunAsync(classifyToChunk.Reader, chunkToEmbed.Writer, progress, onProgress, cts);
        var embedTask = repositories.Embed.RunAsync(chunkToEmbed.Reader,
                                                    embedToIndex.Writer,
                                                    progress,
                                                    onProgress,
                                                    cts);
        var indexTask = mIndexStage.RunAsync(profile,
                                             operationJob,
                                             auditCtx,
                                             embedToIndex.Reader,
                                             progress,
                                             onProgress,
                                             cts
                                            );

        try
        {
            await Task.WhenAll(crawlTask, classifyTask, chunkTask, embedTask, indexTask);
        }
        catch(OperationCanceledException)
        {
            mBroadcaster.RecordJobCancelled(progress.Id);
            throw;
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Pipeline failed for {LibraryId} v{Version}", job.LibraryId, job.Version);
            progress.PipelineState = nameof(ScrapeJobStatus.Failed);
            progress.ErrorMessage = ex.Message;
            onProgress?.Invoke(progress);
            mBroadcaster.RecordJobFailed(progress.Id, ex.Message);
            throw;
        }

        ValidateCandidateCompleteness(progress);
        await repositories.Finalizer.RunAsync(operationJob, progress, profile, ct);

        progress.PipelineState = nameof(ScrapeJobStatus.Completed);
        onProgress?.Invoke(progress);
        mBroadcaster.RecordJobCompleted(progress.Id, progress.PagesCompleted);

        mLogger.LogInformation("Streaming ingestion complete for {LibraryId} v{Version}: {Pages} pages, {Chunks} chunks searchable",
                               job.LibraryId,
                               job.Version,
                               progress.PagesCompleted,
                               progress.ChunksCompleted
                              );
    }

    private async Task WriteBuildingVersionAsync(ScrapeJob job,
                                                  ILibraryRepository libraries,
                                                  CancellationToken ct)
    {
        var version = CreateLifecycleVersion(job, VersionPublicationState.Building, publicationError: null);
        await libraries.UpsertVersionAsync(version, ct);
    }

    private async Task MarkCandidateFailedAsync(ScrapeJob job,
                                                string? profile,
                                                string publicationError,
                                                ILibraryIngestionModeLease modeLease,
                                                ProfileRepositories repositories)
    {
        bool renewed = await modeLease.TryRenewAsync(CancellationToken.None);
        if (!renewed)
            throw new InvalidOperationException(WebModeLeaseLostCleanupMessage);

        CancellationToken ownership = modeLease.OwnershipLostToken;
        var failures = new List<Exception>();
        var failed = CreateLifecycleVersion(job, VersionPublicationState.Failed, publicationError);
        await TryCandidateCleanupStepAsync(
            () => repositories.Libraries.UpsertVersionAsync(failed, ownership),
            FailedDiagnosticsCleanupDescription,
            failures);
        await TryCandidateCleanupStepAsync(
            () => DeleteCandidateBm25Async(repositories.Shards, job, ownership),
            Bm25CleanupDescription,
            failures);
        await TryCandidateCleanupStepAsync(
            () => DeleteCandidateLibraryIndexAsync(repositories.Indexes, job, ownership),
            LibraryIndexCleanupDescription,
            failures);
        await TryCandidateCleanupStepAsync(
            () => mVectorSearch.RemoveIndexAsync(profile, job.LibraryId, job.Version, ownership),
            VectorIndexCleanupDescription,
            failures);
        await TryCandidateCleanupStepAsync(
            () => DeleteCandidateSourceVersionAsync(repositories.Sources, job, ownership),
            SourceDocumentCleanupDescription,
            failures);

        if (failures.Count > 0)
            throw new AggregateException(CandidateCleanupFailureMessage, failures);
    }

    private async Task MarkCandidateFailedPreservingOriginalAsync(ScrapeJob job,
                                                                   string? profile,
                                                                   string publicationError,
                                                                   Exception originalException,
                                                                   ILibraryIngestionModeLease modeLease,
                                                                   ProfileRepositories repositories)
    {
        try
        {
            await MarkCandidateFailedAsync(job, profile, publicationError, modeLease, repositories);
        }
        catch(Exception cleanupException)
        {
            originalException.Data[CandidateCleanupFailureDataKey] = cleanupException.ToString();
            mLogger.LogError(cleanupException,
                             "Candidate cleanup failed for {LibraryId} v{Version}; preserving the original ingestion failure: {OriginalFailure}",
                             job.LibraryId,
                             job.Version,
                             originalException.Message);
        }
    }

    private static async Task DeleteCandidateBm25Async(IBm25ShardRepository shards,
                                                       ScrapeJob job,
                                                       CancellationToken ct)
    {
        await shards.DeleteAsync(job.LibraryId, job.Version, ct);
    }

    private static async Task DeleteCandidateLibraryIndexAsync(ILibraryIndexRepository indexes,
                                                               ScrapeJob job,
                                                               CancellationToken ct)
    {
        await indexes.DeleteAsync(job.LibraryId, job.Version, ct);
    }

    private static async Task DeleteCandidateSourceVersionAsync(ISourceDocumentRepository sourceDocumentRepository,
                                                                ScrapeJob job,
                                                                CancellationToken ct)
    {
        await sourceDocumentRepository.DeleteVersionAsync(job.LibraryId,
                                                           job.Version,
                                                           ct);
    }

    private static async Task TryCandidateCleanupStepAsync(Func<Task> cleanupStep,
                                                           string description,
                                                           List<Exception> failures)
    {
        try
        {
            await cleanupStep();
        }
        catch(Exception ex)
        {
            failures.Add(new InvalidOperationException($"{description}: {ex.Message}", ex));
        }
    }

    private LibraryVersionRecord CreateLifecycleVersion(ScrapeJob job,
                                                         VersionPublicationState state,
                                                         string? publicationError) =>
        new()
            {
                Id = $"{job.LibraryId}/{job.Version}",
                LibraryId = job.LibraryId,
                Version = job.Version,
                ScrapedAt = DateTime.UtcNow,
                PageCount = 0,
                ChunkCount = 0,
                EmbeddingProviderId = mEmbeddingProvider.ProviderId,
                EmbeddingModelName = mEmbeddingProvider.ModelName,
                EmbeddingDimensions = mEmbeddingProvider.Dimensions,
                PublicationState = state,
                PublicationError = publicationError
            };

    private static void ValidateCandidateCompleteness(ScrapeJobRecord progress)
    {
        if (CrawlOutcomeEvaluator.IndicatesFailedCrawl(progress.PagesCompleted, progress.ErrorCount))
        {
            throw new InvalidOperationException(
                $"Crawl harvested {progress.PagesCompleted} page(s) with {progress.ErrorCount} page error(s); " +
                CandidateCannotBePublishedMessage);
        }

        if (progress.ChunksGenerated != progress.ChunksEmbedded ||
            progress.ChunksEmbedded != progress.ChunksCompleted)
        {
            throw new InvalidOperationException(
                $"Candidate chunk pipeline is incomplete: generated={progress.ChunksGenerated}, " +
                $"embedded={progress.ChunksEmbedded}, indexed={progress.ChunksCompleted}.");
        }
    }

    private const int PageChannelCapacity = 50;
    private const int ChunkChannelCapacity = 20;
    private const string PublicationCancelledMessage = "Publication cancelled.";
    private const string WebModeConflictDetail = "or another web-ingestion operation.";
    private const string WebModeLeaseLostCleanupMessage =
        "Candidate cleanup stopped because web ingestion no longer owns its source-mode lease.";
    private const string CandidateCannotBePublishedMessage = "the candidate cannot be published.";
    private const string CandidateCleanupFailureMessage = "One or more candidate cleanup operations failed.";
    private const string CandidateCleanupFailureDataKey = "SaddleRAG.CandidateCleanupFailure";
    private const string PublishedVersionReuseInstruction = "Use a new version identifier for the next manual scan.";
    private const string FailedDiagnosticsCleanupDescription = "record Failed publication diagnostics";
    private const string Bm25CleanupDescription = "delete candidate BM25 shards";
    private const string LibraryIndexCleanupDescription = "delete candidate library index";
    private const string VectorIndexCleanupDescription = "remove candidate vector index";
    private const string SourceDocumentCleanupDescription = "delete candidate source-document version";

    private const string SinglePageStatusIndexed = "Indexed";
    private const string SinglePageStatusEmpty = "Empty";
    private const string SinglePageStatusFailed = "Failed";

    #region Dry run

    /// <summary>
    ///     Run the streaming pipeline for a dry-run scrape. Same crawl,
    ///     classify, chunk, and embed stages as <see cref="IngestAsync" />,
    ///     but every Upsert call is skipped (persistence mode DryRun) and
    ///     the index stage and finalizer are omitted. Returns a
    ///     <see cref="DryRunReport" /> built from an in-memory accumulator
    ///     populated by the stages.
    /// </summary>
    public async Task<DryRunReport> DryRunAsync(ScrapeJob job,
                                                string libraryId,
                                                string version,
                                                string jobId,
                                                Action<int, int>? onProgress = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var startTime = DateTime.UtcNow;
        var acc = new DryRunAccumulator();

        var crawlToClassify = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var classifyToChunk = Channel.CreateBounded<PageRecord>(new BoundedChannelOptions(PageChannelCapacity)
                                                                    { FullMode = BoundedChannelFullMode.Wait }
                                                               );
        var chunkToEmbed = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );
        var embedToDrain = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(ChunkChannelCapacity)
                                                                 { FullMode = BoundedChannelFullMode.Wait }
                                                            );

        var progress = new ScrapeJobRecord
                           {
                               Id = jobId,
                               Job = job
                           };
        progress.PipelineState = nameof(ScrapeJobStatus.Running);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int maxPagesForCallback = job.MaxPages > 0 ? job.MaxPages : 0;

        // Honor caller-supplied seed URLs in the dry-run path too, so a
        // pre-scrape preview shows what a real scrape would discover with
        // the same multi-seed config. resumeUrls stays null because
        // dry-run never persists pages.
        IReadOnlyList<string>? dryRunSeedUrls = null;
        if (job.SeedUrls is { Count: > 0 })
            dryRunSeedUrls = job.SeedUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

        var crawlTask = mCrawlStage.RunAsync(job,
                                             crawlToClassify.Writer,
                                             resumeUrls: null,
                                             seedUrls: dryRunSeedUrls,
                                             progress,
                                             updatedProgress =>
                                             {
                                                 onProgress?.Invoke(updatedProgress.PagesFetched,
                                                                    maxPagesForCallback > 0
                                                                        ? maxPagesForCallback
                                                                        : updatedProgress.PagesFetched
                                                                   );
                                             },
                                             cts,
                                             IngestionPersistenceMode.DryRun,
                                             acc
                                            );

        var classifyTask = mClassifyStage.RunAsync(job,
                                                   crawlToClassify.Reader,
                                                   classifyToChunk.Writer,
                                                   progress,
                                                   onProgress: null,
                                                   cts,
                                                   IngestionPersistenceMode.DryRun,
                                                   acc
                                                  );

        var chunkTask = mChunkStage.RunAsync(classifyToChunk.Reader,
                                             chunkToEmbed.Writer,
                                             progress,
                                             onProgress: null,
                                             cts,
                                             acc
                                            );

        var embedTask = mEmbedStage.RunAsync(chunkToEmbed.Reader,
                                             embedToDrain.Writer,
                                             progress,
                                             onProgress: null,
                                             cts,
                                             IngestionPersistenceMode.DryRun,
                                             acc
                                            );

        var drain = new DrainStage();
        var drainTask = drain.RunAsync(embedToDrain.Reader, cts.Token);

        mBroadcaster.RecordJobStarted(progress.Id, job.LibraryId, job.Version, job.RootUrl ?? string.Empty);

        try
        {
            await Task.WhenAll(crawlTask, classifyTask, chunkTask, embedTask, drainTask);
        }
        catch(OperationCanceledException)
        {
            progress.PipelineState = nameof(ScrapeJobStatus.Cancelled);
            mBroadcaster.RecordJobCancelled(progress.Id);
            throw;
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogError(ex, "Dry-run pipeline failed for {LibraryId} v{Version}", libraryId, version);
            progress.PipelineState = nameof(ScrapeJobStatus.Failed);
            progress.ErrorMessage = ex.Message;
            mBroadcaster.RecordJobFailed(progress.Id, ex.Message);
            throw;
        }

        var snapshot = acc.Snapshot();
        var elapsed = DateTime.UtcNow - startTime;

        var result = new DryRunReport
                         {
                             TotalPages = snapshot.TotalPages,
                             InScopePages = snapshot.InScopePages,
                             OutOfScopePages = snapshot.OutOfScopePages,
                             DepthLimitedSkips = snapshot.DepthLimitedSkips,
                             FilteredSkips = snapshot.FilteredSkips,
                             FetchErrors = snapshot.FetchErrors,
                             DepthDistribution = snapshot.DepthDistribution,
                             PagesByHost = snapshot.PagesByHost,
                             GitHubReposToClone = snapshot.GitHubRepos,
                             SamplePages = snapshot.SamplePages,
                             Errors = snapshot.Errors,
                             ElapsedTime = elapsed,
                             HitMaxPagesLimit = job.MaxPages > 0 && snapshot.TotalPages >= job.MaxPages,
                             PagesRemainingInQueue = 0,
                             SamplePendingUrls = [],
                             DetectedRenderMode = snapshot.RenderMode,
                             MedianContentNodeDelta = snapshot.MedianContentNodeDelta,
                             LoadWaitRecommended = snapshot.LoadWaitRecommended,
                             CategoryHistogram = snapshot.CategoryHistogram,
                             StageTimings = snapshot.Timings,
                             Escalation = snapshot.Escalation
                         };

        progress.PipelineState = nameof(ScrapeJobStatus.Completed);
        mBroadcaster.RecordJobCompleted(progress.Id, snapshot.TotalPages);

        mLogger.LogInformation("Dry run complete for {LibraryId} v{Version}: {Total} pages in {Elapsed}s — " +
                               "fetch={FetchMs}ms ({FetchCount} samples) classify={ClassifyMs}ms ({ClassifyCount}) " +
                               "chunk={ChunkMs}ms ({ChunkCount}) embed={EmbedMs}ms ({EmbedCount} batches)",
                               libraryId,
                               version,
                               snapshot.TotalPages,
                               elapsed.TotalSeconds,
                               snapshot.Timings.TotalFetchMs,
                               snapshot.Timings.FetchSampleCount,
                               snapshot.Timings.TotalClassifyMs,
                               snapshot.Timings.ClassifySampleCount,
                               snapshot.Timings.TotalChunkMs,
                               snapshot.Timings.ChunkSampleCount,
                               snapshot.Timings.TotalEmbedMs,
                               snapshot.Timings.EmbedBatchCount
                              );

        return result;
    }

    #endregion

    #region Single-page top-up

    /// <summary>
    ///     Ingest one URL into an existing (library, version) without
    ///     re-crawling. Fetches the page through the same Playwright
    ///     path as a regular scrape, classifies it, chunks it, embeds
    ///     the chunks, upserts them, and refreshes the BM25 index over
    ///     the full chunk corpus so search picks the new content up
    ///     immediately.
    /// </summary>
    public async Task<SinglePageIngestResult> IngestSinglePageAsync(string libraryId,
                                                                    string version,
                                                                    string url,
                                                                    string? profile = null,
                                                                    CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(url);
        await using ILibraryIngestionModeLease modeLease = await AcquireWebModeLeaseAsync(profile, libraryId, ct);
        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(ct, modeLease.OwnershipLostToken);
        await CommitWebOwnershipAsync(profile, libraryId, modeLease, operation.Token);
        ProfileRepositories repositories = ResolveRepositories(profile);

        mLogger.LogInformation("Adding single page {Url} to {LibraryId} v{Version}", url, libraryId, version);

        var page = await mCrawler.FetchSinglePageForProfileAsync(libraryId,
                                                                  version,
                                                                  url,
                                                                  profile,
                                                                  operation.Token);

        SinglePageIngestResult result;
        if (page == null)
        {
            result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusFailed,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             Reason = "Fetch failed after retries (likely WAF block or persistent error)."
                         };
        }
        else
            result = await ProcessSinglePageAsync(page,
                                                  libraryId,
                                                  version,
                                                  url,
                                                  repositories,
                                                  operation.Token);

        return result;
    }

    private async Task<ILibraryIngestionModeLease> AcquireWebModeLeaseAsync(string? profile,
                                                                            string libraryId,
                                                                            CancellationToken ct)
    {
        ILibraryIngestionModeLease? modeLease = await mModeLeaseManager.TryAcquireAsync(profile,
                                                                                         libraryId,
                                                                                         LibraryIngestionMode.Web,
                                                                                         ct);
        if (modeLease == null)
            throw new InvalidOperationException($"Library '{libraryId}' is owned by directory ingestion " +
                                                WebModeConflictDetail);
        return modeLease;
    }

    private async Task CommitWebOwnershipAsync(string? profile,
                                               string libraryId,
                                               ILibraryIngestionModeLease modeLease,
                                               CancellationToken ct)
    {
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryId, ct);
        if (definition != null)
        {
            if (modeLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved)
                await modeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory,
                                                               CancellationToken.None);
            throw new InvalidOperationException(
                $"Library '{libraryId}' is registered for directory ingestion and cannot be mutated by web ingestion.");
        }


        if (modeLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved)
        {
            ILibraryIngestionModeRepository modes =
                mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
            LibraryIngestionDataEvidence evidence = await modes.GetLibraryDataEvidenceAsync(libraryId, ct);
            if (evidence.HasDirectoryDefinition)
            {
                bool reconciled = await modeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory, ct);
                if (!reconciled)
                    throw new InvalidOperationException(
                        "The web ingestion operation no longer owns its source-mode lease.");
                throw new InvalidOperationException(
                    $"Library '{libraryId}' contains directory-ingestion data and cannot be mutated by web ingestion.");
            }

            if (!evidence.HasLibraryRecord &&
                (evidence.HasDocumentLifecycleData || evidence.HasChildContentData))
                throw new InvalidOperationException(
                    $"Library '{libraryId}' has unclassified partial data and cannot be mutated by web ingestion.");
        }

        bool committed = await modeLease.TryCommitAsync(ct);
        if (!committed)
            throw new InvalidOperationException("The web ingestion operation no longer owns its source-mode lease.");
    }

    private async Task<SinglePageIngestResult> ProcessSinglePageAsync(PageRecord page,
                                                                      string libraryId,
                                                                      string version,
                                                                      string url,
                                                                      ProfileRepositories repositories,
                                                                      CancellationToken ct)
    {
        // Reuse the streaming pipeline's per-page classify + embed primitives
        // so the single-page path can't drift on prompt format, confidence
        // threshold, or retry semantics. The orchestrator owns only the
        // single-page result shape and BM25 refresh.
        var classified = await repositories.Classify.ClassifyPageAsync(page, libraryId);
        var chunks = mChunker.Chunk(classified);

        SinglePageIngestResult result;
        if (chunks.Count == 0)
        {
            result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusEmpty,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             Reason = "Page fetched but produced zero chunks (empty or filtered content)."
                         };
        }
        else
        {
            var embedded = await EmbedStage.EmbedBatchAsync(mEmbeddingProvider, mLogger, chunks, ct);
            await repositories.Chunks.UpsertChunksAsync(embedded, ct);

            var bm25Job = new ScrapeJob
                              {
                                  RootUrl = url,
                                  LibraryId = libraryId,
                                  Version = version,
                                  LibraryHint = libraryId,
                                  AllowedUrlPatterns = []
                              };
            await repositories.Finalizer.BuildBm25IndexAsync(bm25Job, ct);

            result = new SinglePageIngestResult
                         {
                             Status = SinglePageStatusIndexed,
                             Url = url,
                             Library = libraryId,
                             Version = version,
                             ChunksAdded = embedded.Length,
                             Category = classified.Category.ToString()
                         };
        }

        return result;
    }

    private ProfileRepositories ResolveRepositories(string? profile)
    {
        ProfileRepositories result;
        if (string.IsNullOrEmpty(profile))
        {
            result = new ProfileRepositories(mLibraryRepository,
                                             mPageRepository,
                                             mChunkRepository,
                                             mLibraryProfileRepository,
                                             mLibraryIndexRepository,
                                             mBm25ShardRepository,
                                             mSourceDocumentRepository,
                                             mClassifyStage,
                                             mEmbedStage,
                                             mFinalizer);
        }
        else
        {
            ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
            IPageRepository pages = mRepositoryFactory.GetPageRepository(profile);
            IChunkRepository chunks = mRepositoryFactory.GetChunkRepository(profile);
            ILibraryProfileRepository profiles = mRepositoryFactory.GetLibraryProfileRepository(profile);
            ILibraryIndexRepository indexes = mRepositoryFactory.GetLibraryIndexRepository(profile);
            IBm25ShardRepository shards = mRepositoryFactory.GetBm25ShardRepository(profile);
            ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
            var classify = new ClassifyStage(mPageProcessor, pages, mBroadcaster, mLogger);
            var embed = new EmbedStage(mPageProcessor, chunks, mBroadcaster, mLogger);
            var finalizer = new IngestionFinalizer(chunks,
                                                   shards,
                                                   indexes,
                                                   libraries,
                                                   mVectorSearch,
                                                   mEmbeddingProvider,
                                                   profiles,
                                                   mSuspectDetector,
                                                   mLlmClassifier,
                                                   mPageProcessor,
                                                   mLogger,
                                                   sources);
            result = new ProfileRepositories(libraries,
                                             pages,
                                             chunks,
                                             profiles,
                                             indexes,
                                             shards,
                                             sources,
                                             classify,
                                             embed,
                                             finalizer);
        }

        return result;
    }

    private sealed record ProfileRepositories(ILibraryRepository Libraries,
                                              IPageRepository Pages,
                                              IChunkRepository Chunks,
                                              ILibraryProfileRepository Profiles,
                                              ILibraryIndexRepository Indexes,
                                              IBm25ShardRepository Shards,
                                              ISourceDocumentRepository Sources,
                                              ClassifyStage Classify,
                                              EmbedStage Embed,
                                              IngestionFinalizer Finalizer);

    #endregion

}
