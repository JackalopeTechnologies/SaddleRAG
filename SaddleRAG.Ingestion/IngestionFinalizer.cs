// IngestionFinalizer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Suspect;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Post-pipeline finalization for a scrape job. Once all five streaming
///     stages have completed, the finalizer (a) rebuilds the BM25 inverted
///     index over the freshly persisted chunks, (b) upserts the
///     <see cref="LibraryRecord" /> and <see cref="LibraryVersionRecord" />
///     metadata, and (c) evaluates the version against the suspect-detector
///     rules. These steps are sequential and not channel-driven, so they
///     don't fit the <c>IStage</c>-shaped contract that <see cref="CrawlStage" />
///     and friends follow — they live in their own type so the orchestrator
///     stays a thin wire harness over the stage classes.
/// </summary>
internal sealed class IngestionFinalizer
{
    public IngestionFinalizer(IChunkRepository chunkRepository,
                              IBm25ShardRepository bm25ShardRepository,
                              ILibraryIndexRepository libraryIndexRepository,
                              ILibraryRepository libraryRepository,
                              IEmbeddingProvider embeddingProvider,
                              ILibraryProfileRepository libraryProfileRepository,
                              SuspectDetector suspectDetector,
                              ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(bm25ShardRepository);
        ArgumentNullException.ThrowIfNull(libraryIndexRepository);
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(libraryProfileRepository);
        ArgumentNullException.ThrowIfNull(suspectDetector);
        ArgumentNullException.ThrowIfNull(logger);
        mChunkRepository = chunkRepository;
        mBm25ShardRepository = bm25ShardRepository;
        mLibraryIndexRepository = libraryIndexRepository;
        mLibraryRepository = libraryRepository;
        mEmbeddingProvider = embeddingProvider;
        mLibraryProfileRepository = libraryProfileRepository;
        mSuspectDetector = suspectDetector;
        mLogger = logger;
    }

    private readonly IBm25ShardRepository mBm25ShardRepository;
    private readonly IChunkRepository mChunkRepository;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILibraryIndexRepository mLibraryIndexRepository;
    private readonly ILibraryProfileRepository mLibraryProfileRepository;
    private readonly ILibraryRepository mLibraryRepository;
    private readonly ILogger mLogger;
    private readonly SuspectDetector mSuspectDetector;

    /// <summary>
    ///     Run the full post-pipeline finalization: BM25 build → library +
    ///     version metadata upsert → suspect evaluation.
    /// </summary>
    public async Task RunAsync(ScrapeJob job, ScrapeJobRecord progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);

        await BuildBm25IndexAsync(job, ct);
        await UpdateLibraryMetadataAsync(job, progress, ct);
    }

    /// <summary>
    ///     Build the sharded BM25 inverted index over the chunks persisted
    ///     for this (library, version), then upsert the matching
    ///     <see cref="LibraryIndex" /> with the inline stats. If a prior
    ///     index exists, its <c>CodeFenceSymbols</c> and <c>Manifest</c>
    ///     fields are preserved so a re-scrape doesn't blow away
    ///     symbol-extraction state until the next rescrub recomputes it.
    ///     Exposed as <c>internal</c> so the single-page ingest path on
    ///     <see cref="IngestionOrchestrator" /> can refresh the BM25 index
    ///     after adding one page.
    /// </summary>
    internal async Task BuildBm25IndexAsync(ScrapeJob job, CancellationToken ct)
    {
        var chunks = await mChunkRepository.GetChunksAsync(job.LibraryId, job.Version, ct);
        if (chunks.Count == 0)
        {
            mLogger.LogWarning("BM25 build skipped for {LibraryId} v{Version}: no chunks persisted",
                               job.LibraryId,
                               job.Version
                              );
        }
        else
            await PersistBm25IndexAsync(job, chunks, ct);
    }

    private async Task PersistBm25IndexAsync(ScrapeJob job, IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        var build = Bm25IndexBuilder.Build(job.LibraryId, job.Version, chunks);
        await mBm25ShardRepository.ReplaceShardsAsync(job.LibraryId, job.Version, build.Shards, ct);

        var existing = await mLibraryIndexRepository.GetAsync(job.LibraryId, job.Version, ct);
        var index = new LibraryIndex
                        {
                            Id = LibraryIndexRepository.MakeId(job.LibraryId, job.Version),
                            LibraryId = job.LibraryId,
                            Version = job.Version,
                            Bm25 = build.Stats,
                            CodeFenceSymbols = existing?.CodeFenceSymbols ?? [],
                            Manifest = existing?.Manifest ?? new LibraryManifest()
                        };
        await mLibraryIndexRepository.UpsertAsync(index, ct);

        mLogger.LogInformation("BM25 index built for {LibraryId} v{Version}: {Docs} docs, {Shards} shards, avgLen={AvgLen:F1}",
                               job.LibraryId,
                               job.Version,
                               build.Stats.DocumentCount,
                               build.Stats.ShardCount,
                               build.Stats.AverageDocLength
                              );
    }

    private async Task UpdateLibraryMetadataAsync(ScrapeJob job, ScrapeJobRecord progress, CancellationToken ct)
    {
        var library = await mLibraryRepository.GetLibraryAsync(job.LibraryId, ct);
        if (library == null)
        {
            library = new LibraryRecord
                          {
                              Id = job.LibraryId,
                              Name = job.LibraryId,
                              Hint = job.LibraryHint,
                              CurrentVersion = job.Version,
                              AllVersions = [job.Version]
                          };
        }
        else
        {
            library.CurrentVersion = job.Version;
            if (!library.AllVersions.Contains(job.Version))
                library.AllVersions.Add(job.Version);
        }

        await mLibraryRepository.UpsertLibraryAsync(library, ct);

        var versionRecord = new LibraryVersionRecord
                                {
                                    Id = $"{job.LibraryId}/{job.Version}",
                                    LibraryId = job.LibraryId,
                                    Version = job.Version,
                                    ScrapedAt = DateTime.UtcNow,
                                    PageCount = progress.PagesFetched,
                                    ChunkCount = progress.ChunksCompleted,
                                    EmbeddingProviderId = mEmbeddingProvider.ProviderId,
                                    EmbeddingModelName = mEmbeddingProvider.ModelName,
                                    EmbeddingDimensions = mEmbeddingProvider.Dimensions
                                };
        await mLibraryRepository.UpsertVersionAsync(versionRecord, ct);
        await EvaluateSuspectAsync(job, progress, ct);
    }

    private async Task EvaluateSuspectAsync(ScrapeJob job, ScrapeJobRecord progress, CancellationToken ct)
    {
        var languageMix = await mChunkRepository.GetLanguageMixAsync(job.LibraryId, job.Version, ct);
        var hostnameDist = await mChunkRepository.GetHostnameDistributionAsync(job.LibraryId, job.Version, ct);
        var sampleTitles =
            await mChunkRepository.GetSampleTitlesAsync(job.LibraryId, job.Version, SuspectSampleTitleLimit, ct);

        var profile = await mLibraryProfileRepository.GetAsync(job.LibraryId, job.Version, ct);
        var declaredLanguages = profile?.Languages ?? [];

        // distinctLinkTargets: SparseLinkGraph disabled until an outbound-link count helper exists.
        // Passed as int.MaxValue so it never triggers; the other four reasons cover the common cases.
        var reasons = await mSuspectDetector.EvaluateAsync(job.LibraryId,
                                                           job.Version,
                                                           job.RootUrl,
                                                           progress.PagesCompleted,
                                                           hostnameDist.Count,
                                                           int.MaxValue,
                                                           languageMix,
                                                           declaredLanguages,
                                                           sampleTitles,
                                                           ct
                                                          );

        if (reasons.Count > 0)
            await mLibraryRepository.SetSuspectAsync(job.LibraryId, job.Version, reasons, ct);
        else
            await mLibraryRepository.ClearSuspectAsync(job.LibraryId, job.Version, ct);
    }

    private const int SuspectSampleTitleLimit = 5;
}
