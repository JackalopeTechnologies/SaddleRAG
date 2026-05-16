// IngestionOrchestrator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
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
    public IngestionOrchestrator(PageCrawler crawler,
                                 LlmClassifier llmClassifier,
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
                                 ILogger<IngestionOrchestrator> logger)
    {
        mCrawler = crawler;
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mBroadcaster = broadcaster;
        mLogger = logger;
        mCrawlStage = new CrawlStage(crawler, logger);
        mClassifyStage = new ClassifyStage(llmClassifier, pageRepository, broadcaster, logger);
        mChunkStage = new ChunkStage(chunker, broadcaster, logger);
        mEmbedStage = new EmbedStage(embeddingProvider, chunkRepository, broadcaster, logger);
        mIndexStage = new IndexStage(vectorSearch, auditWriter, broadcaster, logger);
        mFinalizer = new IngestionFinalizer(chunkRepository,
                                            bm25ShardRepository,
                                            libraryIndexRepository,
                                            libraryRepository,
                                            embeddingProvider,
                                            libraryProfileRepository,
                                            suspectDetector,
                                            logger
                                           );
    }

    private readonly IMonitorBroadcaster mBroadcaster;

    private readonly CategoryAwareChunker mChunker;
    private readonly IChunkRepository mChunkRepository;
    private readonly ChunkStage mChunkStage;
    private readonly ClassifyStage mClassifyStage;

    private readonly PageCrawler mCrawler;
    private readonly CrawlStage mCrawlStage;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly EmbedStage mEmbedStage;
    private readonly IngestionFinalizer mFinalizer;
    private readonly IndexStage mIndexStage;
    private readonly ILogger<IngestionOrchestrator> mLogger;
    private readonly IPageRepository mPageRepository;

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

        mLogger.LogInformation("Starting streaming ingestion for {LibraryId} v{Version}", job.LibraryId, job.Version);

        // Build resume URL set from existing pages in DB. SeedFromStoredPages
        // flips this from "skip already-fetched URLs" to "use stored URLs as
        // extra crawl seeds and re-fetch every one of them."
        var existingPages = await mPageRepository.GetPagesAsync(job.LibraryId, job.Version, ct);
        IReadOnlySet<string>? resumeUrls = null;
        IReadOnlyList<string>? seedUrls = null;

        if (existingPages.Count > 0 && job.SeedFromStoredPages)
        {
            seedUrls = existingPages.Select(p => p.Url).ToList();
            mLogger.LogInformation("Seed-from-stored-pages mode: {Count} stored URLs will be re-fetched",
                                   seedUrls.Count
                                  );
        }

        if (existingPages.Count > 0 && !job.SeedFromStoredPages && !forceClean)
        {
            resumeUrls = existingPages.Select(p => p.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            mLogger.LogInformation("Resume mode: {Count} existing pages found", resumeUrls.Count);
        }

        // On force re-scrape, clear existing chunks before pipeline starts
        if (forceClean)
        {
            await mChunkRepository.DeleteChunksAsync(job.LibraryId, job.Version, ct);
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

        // Shared progress record
        var progress = jobRecord ??
                       new ScrapeJobRecord
                           {
                               Id = Guid.NewGuid().ToString(),
                               Job = job,
                               Profile = profile
                           };
        progress.PipelineState = nameof(ScrapeJobStatus.Running);

        var auditCtx = new AuditContext
                           {
                               JobId = progress.Id,
                               LibraryId = job.LibraryId,
                               Version = job.Version
                           };

        mBroadcaster.RecordJobStarted(progress.Id, job.LibraryId, job.Version, job.RootUrl ?? string.Empty);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Launch all five stages
        var crawlTask = mCrawlStage.RunAsync(job,
                                             crawlToClassify.Writer,
                                             resumeUrls,
                                             seedUrls,
                                             progress,
                                             onProgress,
                                             cts
                                            );
        var classifyTask = mClassifyStage.RunAsync(job,
                                                   crawlToClassify.Reader,
                                                   classifyToChunk.Writer,
                                                   progress,
                                                   onProgress,
                                                   cts
                                                  );
        var chunkTask = mChunkStage.RunAsync(classifyToChunk.Reader, chunkToEmbed.Writer, progress, onProgress, cts);
        var embedTask = mEmbedStage.RunAsync(chunkToEmbed.Reader, embedToIndex.Writer, progress, onProgress, cts);
        var indexTask = mIndexStage.RunAsync(profile,
                                             job,
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

        // BM25 build + library metadata upsert + suspect evaluation.
        await mFinalizer.RunAsync(job, progress, ct);

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

    private const int PageChannelCapacity = 50;
    private const int ChunkChannelCapacity = 20;

    private const string SinglePageStatusIndexed = "Indexed";
    private const string SinglePageStatusEmpty = "Empty";
    private const string SinglePageStatusFailed = "Failed";

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

        mLogger.LogInformation("Adding single page {Url} to {LibraryId} v{Version}", url, libraryId, version);

        var page = await mCrawler.FetchSinglePageAsync(libraryId, version, url, ct);

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
            result = await ProcessSinglePageAsync(page, libraryId, version, url, ct);

        return result;
    }

    private async Task<SinglePageIngestResult> ProcessSinglePageAsync(PageRecord page,
                                                                      string libraryId,
                                                                      string version,
                                                                      string url,
                                                                      CancellationToken ct)
    {
        // Reuse the streaming pipeline's per-page classify + embed primitives
        // so the single-page path can't drift on prompt format, confidence
        // threshold, or retry semantics. The orchestrator owns only the
        // single-page result shape and BM25 refresh.
        var classified = await mClassifyStage.ClassifyPageAsync(page, libraryId);
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
            await mChunkRepository.UpsertChunksAsync(embedded, ct);

            var bm25Job = new ScrapeJob
                              {
                                  RootUrl = url,
                                  LibraryId = libraryId,
                                  Version = version,
                                  LibraryHint = libraryId,
                                  AllowedUrlPatterns = []
                              };
            await mFinalizer.BuildBm25IndexAsync(bm25Job, ct);

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

    #endregion

}
