// ReembedService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Recon;

/// <summary>
///     Workhorse for reembed_library. Re-embeds existing chunks via the
///     currently configured IEmbeddingProvider without re-crawling pages,
///     re-running the chunker, or touching parser-derived metadata.
///     Used when swapping embedding providers (Ollama → TEI) or models
///     (nomic-embed-text → mxbai-embed-large), so existing libraries get
///     vectors that are compatible with live query embeddings.
///     What it does:
///     1. Load stored chunks for (libraryId, version) from the chunk repo.
///     2. Re-embed each chunk's Content via the active IEmbeddingProvider.
///     3. Upsert the updated chunks back to MongoDB.
///     4. Reload the in-memory vector index.
///     5. Update LibraryVersionRecord.EmbeddingProviderId / ModelName /
///        Dimensions so future queries pick the right embedding shape.
///     What it does NOT do:
///     - Re-crawl pages (that's scrape_docs / rescrape_library).
///     - Re-run the chunker (that's rechunk_library).
///     - Refresh symbols / categories (that's reextract_library).
/// </summary>
public class ReembedService
{
    public ReembedService(IEmbeddingProvider embeddingProvider,
                          IVectorSearchProvider vectorSearch,
                          ILogger<ReembedService> logger)
    {
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(logger);

        mEmbeddingProvider = embeddingProvider;
        mVectorSearch = vectorSearch;
        mLogger = logger;
    }

    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILogger<ReembedService> mLogger;
    private readonly IVectorSearchProvider mVectorSearch;

    /// <summary>
    ///     Re-embed every chunk for (libraryId, version). Idempotent —
    ///     running it twice in a row produces the same end state.
    /// </summary>
    public async Task<ReembedResult> ReembedAsync(string? profile,
                                                   IChunkRepository chunkRepo,
                                                   ILibraryRepository libraryRepo,
                                                   string libraryId,
                                                   string version,
                                                   ReembedOptions options,
                                                   Action<int, int>? onProgress = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkRepo);
        ArgumentNullException.ThrowIfNull(libraryRepo);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(options);

        var versionRecord = await libraryRepo.GetVersionAsync(libraryId, version, ct);

        var allChunks = await chunkRepo.GetChunksAsync(libraryId, version, ct);
        var scopedChunks = options.MaxChunks.HasValue
                               ? allChunks.Take(options.MaxChunks.Value).ToList()
                               : allChunks.ToList();

        mLogger.LogInformation("Reembed starting for {LibraryId} v{Version}: {Count} chunks (dry={Dry}, provider={Provider}, model={Model})",
                               libraryId,
                               version,
                               scopedChunks.Count,
                               options.DryRun,
                               mEmbeddingProvider.ProviderId,
                               mEmbeddingProvider.ModelName
                              );

        ReembedResult result;
        if (scopedChunks.Count == 0)
        {
            result = new ReembedResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             Processed = 0,
                             EmbeddingProviderId = mEmbeddingProvider.ProviderId,
                             EmbeddingModelName = mEmbeddingProvider.ModelName,
                             EmbeddingDimensions = mEmbeddingProvider.Dimensions,
                             PreviousEmbeddingProviderId = versionRecord?.EmbeddingProviderId,
                             PreviousEmbeddingModelName = versionRecord?.EmbeddingModelName,
                             PreviousEmbeddingDimensions = versionRecord?.EmbeddingDimensions,
                             DryRun = options.DryRun,
                             NothingToDo = true
                         };
        }
        else
        {
            var processed = options.DryRun
                                ? scopedChunks.Count
                                : await EmbedAndPersistAsync(profile,
                                                              chunkRepo,
                                                              libraryRepo,
                                                              versionRecord,
                                                              libraryId,
                                                              version,
                                                              scopedChunks,
                                                              onProgress,
                                                              ct
                                                             );

            result = new ReembedResult
                         {
                             LibraryId = libraryId,
                             Version = version,
                             Processed = processed,
                             EmbeddingProviderId = mEmbeddingProvider.ProviderId,
                             EmbeddingModelName = mEmbeddingProvider.ModelName,
                             EmbeddingDimensions = mEmbeddingProvider.Dimensions,
                             PreviousEmbeddingProviderId = versionRecord?.EmbeddingProviderId,
                             PreviousEmbeddingModelName = versionRecord?.EmbeddingModelName,
                             PreviousEmbeddingDimensions = versionRecord?.EmbeddingDimensions,
                             DryRun = options.DryRun,
                             NothingToDo = false
                         };
        }

        mLogger.LogInformation("Reembed complete for {LibraryId} v{Version}: processed={Processed}, dry={Dry}",
                               libraryId,
                               version,
                               result.Processed,
                               options.DryRun
                              );

        return result;
    }

    private async Task<int> EmbedAndPersistAsync(string? profile,
                                                  IChunkRepository chunkRepo,
                                                  ILibraryRepository libraryRepo,
                                                  LibraryVersionRecord? versionRecord,
                                                  string libraryId,
                                                  string version,
                                                  List<DocChunk> chunks,
                                                  Action<int, int>? onProgress,
                                                  CancellationToken ct)
    {
        var total = chunks.Count;
        var done = 0;
        var updated = new List<DocChunk>(total);

        foreach(var batch in chunks.Chunk(EmbedBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var texts = batch.Select(c => c.Content).ToList();
            var vectors = await mEmbeddingProvider.EmbedAsync(texts, ct);

            foreach(var (chunk, vector) in batch.Zip(vectors))
            {
                var refreshed = chunk with { Embedding = vector };
                updated.Add(refreshed);
                done++;
            }

            onProgress?.Invoke(done, total);
        }

        await chunkRepo.UpsertChunksAsync(updated, ct);

        try
        {
            await mVectorSearch.IndexChunksAsync(profile, libraryId, version, updated, ct);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex,
                               "Vector index reload failed after reembed; chunks are persisted but search may be stale until next reload_profile"
                              );
        }

        if (versionRecord != null)
        {
            var refreshedVersion = versionRecord with
                                       {
                                           EmbeddingProviderId = mEmbeddingProvider.ProviderId,
                                           EmbeddingModelName = mEmbeddingProvider.ModelName,
                                           EmbeddingDimensions = mEmbeddingProvider.Dimensions
                                       };
            await libraryRepo.UpsertVersionAsync(refreshedVersion, ct);
        }

        return done;
    }

    private const int EmbedBatchSize = 50;
}
