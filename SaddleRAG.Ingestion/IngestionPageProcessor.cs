// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Ingestion;

/// <summary>
///     Source-neutral downstream ingestion boundary shared by web crawl
///     pages and local-document pages.
/// </summary>
public sealed class IngestionPageProcessor
{
    public IngestionPageProcessor(ILlmClassifier classifier,
                                  CategoryAwareChunker chunker,
                                  IEmbeddingProvider embeddingProvider,
                                  IVectorSearchProvider vectorSearch,
                                  ILogger<IngestionPageProcessor> logger)
        : this(classifier, chunker, embeddingProvider, vectorSearch, logger, sharedLoggerCategory: true)
    {
    }

    internal IngestionPageProcessor(ILlmClassifier classifier,
                                    CategoryAwareChunker chunker,
                                    IEmbeddingProvider embeddingProvider,
                                    IVectorSearchProvider vectorSearch,
                                    ILogger logger,
                                    bool sharedLoggerCategory)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(logger);
        mClassifier = classifier;
        mChunker = chunker;
        mEmbeddingProvider = embeddingProvider;
        mVectorSearch = vectorSearch;
        mLogger = logger;
        _ = sharedLoggerCategory;
    }

    private readonly CategoryAwareChunker mChunker;
    private readonly ILlmClassifier mClassifier;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILogger mLogger;
    private readonly IVectorSearchProvider mVectorSearch;

    public string EmbeddingProviderId => mEmbeddingProvider.ProviderId;

    public string EmbeddingModelName => mEmbeddingProvider.ModelName;

    public int EmbeddingDimensions => mEmbeddingProvider.Dimensions;

    public string ClassifierBackend => mClassifier.BackendName;

    public string ClassifierModel => mClassifier.ModelId;

    internal async Task<PageRecord> ClassifyAsync(PageRecord page,
                                                  string libraryHint,
                                                  IPageRepository pageRepository,
                                                  PagePersistenceIntent persistence,
                                                  CancellationToken ct)
    {
        (DocCategory category, float confidence) = await mClassifier.ClassifyAsync(page, libraryHint, ct);
        PageRecord result = category != DocCategory.Unclassified && confidence > 0
            ? page with { Category = category }
            : page;
        bool persist = ShouldPersist(persistence, page, result);
        if (persist)
            await pageRepository.UpsertPageAsync(result, ct);
        return result;
    }

    internal IReadOnlyList<DocChunk> Chunk(PageRecord page) => mChunker.Chunk(page);

    internal async Task<DocChunk[]> EmbedAsync(IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var result = new List<DocChunk>(chunks.Count);
        var offset = 0;
        while(offset < chunks.Count)
        {
            int count = Math.Min(EmbedStage.EmbedBatchSize, chunks.Count - offset);
            IReadOnlyList<DocChunk> batch = chunks.Skip(offset).Take(count).ToList();
            DocChunk[] embedded = await EmbedStage.EmbedBatchAsync(mEmbeddingProvider,
                                                                   mLogger,
                                                                   batch,
                                                                   ct);
            result.AddRange(embedded);
            offset += count;
        }

        return result.ToArray();
    }

    internal async Task<IngestionPageBatchResult> ProcessPagesAsync(
        IReadOnlyList<PageRecord> pages,
        string libraryHint,
        IPageRepository pageRepository,
        IChunkRepository chunkRepository,
        PagePersistenceIntent persistence,
        Func<IReadOnlyList<DocChunk>, IReadOnlyList<DocChunk>>? beforeEmbedding,
        Action<int>? reserveChunks,
        CancellationToken ct)
    {
        var classifiedPages = new List<PageRecord>(pages.Count);
        var chunks = new List<DocChunk>();
        foreach(PageRecord page in pages)
        {
            PageRecord classified = await ClassifyAsync(page,
                                                        libraryHint,
                                                        pageRepository,
                                                        PagePersistenceIntent.None,
                                                        ct);
            classifiedPages.Add(classified);
            chunks.AddRange(Chunk(classified));
        }

        IReadOnlyList<DocChunk> prepared = beforeEmbedding?.Invoke(chunks) ?? chunks;
        reserveChunks?.Invoke(prepared.Count);
        for(var index = 0; index < classifiedPages.Count; index++)
        {
            PageRecord classified = classifiedPages[index];
            if (ShouldPersist(persistence, pages[index], classified))
                await pageRepository.UpsertPageAsync(classified, ct);
        }

        IReadOnlyList<DocChunk> missing = prepared.Where(chunk => chunk.Embedding == null).ToList();
        IReadOnlyList<DocChunk> embedded;
        if (missing.Count == 0)
            embedded = prepared;
        else
        {
            DocChunk[] generated = await EmbedAsync(missing, ct);
            IReadOnlyDictionary<string, DocChunk> byId = generated.ToDictionary(chunk => chunk.Id,
                                                                                 StringComparer.Ordinal);
            embedded = prepared.Select(chunk => byId.TryGetValue(chunk.Id, out DocChunk? value)
                                                    ? value
                                                    : chunk)
                               .ToList();
        }

        if (embedded.Count > 0)
            await chunkRepository.UpsertChunksAsync(embedded, ct);
        return new IngestionPageBatchResult(classifiedPages, embedded);
    }

    private static bool ShouldPersist(PagePersistenceIntent persistence,
                                      PageRecord original,
                                      PageRecord classified) =>
        persistence == PagePersistenceIntent.UpsertAlways ||
        persistence == PagePersistenceIntent.UpdateIfClassified && !ReferenceEquals(classified, original);

    internal async Task PrepareSearchIndexesAsync(string? profile,
                                                  string libraryId,
                                                  string version,
                                                  IReadOnlyList<DocChunk> chunks,
                                                  IBm25ShardRepository shards,
                                                  ILibraryIndexRepository indexes,
                                                  CancellationToken ct)
    {
        Bm25BuildResult build = Bm25IndexBuilder.Build(libraryId, version, chunks);
        await shards.ReplaceShardsAsync(libraryId, version, build.Shards, ct);
        LibraryIndex? existing = await indexes.GetAsync(libraryId, version, ct);
        var index = new LibraryIndex
                        {
                            Id = LibraryIndexRepository.MakeId(libraryId, version),
                            LibraryId = libraryId,
                            Version = version,
                            Bm25 = build.Stats,
                            CodeFenceSymbols = existing?.CodeFenceSymbols ?? [],
                            Manifest = existing?.Manifest ?? new LibraryManifest()
                        };
        await indexes.UpsertAsync(index, ct);
        await mVectorSearch.IndexChunksAsync(profile, libraryId, version, chunks, ct);
    }
}
