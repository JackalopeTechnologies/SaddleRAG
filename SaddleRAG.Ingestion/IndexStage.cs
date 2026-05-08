// IndexStage.cs
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

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Final stage of the streaming ingestion pipeline. Consumes embedded
///     chunks from the embed stage's output channel, rebuilds the vector
///     index in batches, and emits one Indexed audit record per page that
///     reached this stage. The audit emission runs in a finally block so a
///     mid-flight cancellation still produces accurate Fetched -> Indexed
///     transitions for any page whose chunks were committed before cancel.
/// </summary>
internal sealed class IndexStage
{
    public IndexStage(IVectorSearchProvider vectorSearch,
                      IScrapeAuditWriter auditWriter,
                      IMonitorBroadcaster broadcaster,
                      ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        mVectorSearch = vectorSearch;
        mAuditWriter = auditWriter;
        mBroadcaster = broadcaster;
        mLogger = logger;
    }

    private readonly IScrapeAuditWriter mAuditWriter;
    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly ILogger mLogger;
    private readonly IVectorSearchProvider mVectorSearch;

    private const int IndexRebuildInterval = 100;

    /// <summary>
    ///     Drive the index stage to completion or cancellation. Each unique
    ///     page URL whose chunks arrive at this stage gets a single
    ///     RecordIndexed audit entry, even on cancellation or fatal error.
    /// </summary>
    public async Task RunAsync(string? profile,
                               ScrapeJob job,
                               AuditContext auditCtx,
                               ChannelReader<DocChunk[]> input,
                               ScrapeJobRecord progress,
                               Action<ScrapeJobRecord>? onProgress,
                               CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(auditCtx);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cts);

        var pendingChunks = new List<DocChunk>();
        var indexedPageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageChunkCounts =
            new Dictionary<string, (int ChunkCount, DocCategory Category)>(StringComparer.OrdinalIgnoreCase);
        var pageMetadata = new Dictionary<string, (int Depth, string? ParentUrl)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await foreach(var embeddedChunks in input.ReadAllAsync(cts.Token))
            {
                pendingChunks.AddRange(embeddedChunks);
                progress.ChunksCompleted += embeddedChunks.Length;

                AccumulatePageChunkCounts(embeddedChunks, indexedPageUrls, pageChunkCounts, pageMetadata);

                if (pendingChunks.Count >= IndexRebuildInterval)
                {
                    await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                    pendingChunks.Clear();
                    progress.PagesCompleted = indexedPageUrls.Count;
                    onProgress?.Invoke(progress);
                }
            }

            if (pendingChunks.Count > 0)
            {
                await RebuildIndexAsync(profile, job, pendingChunks, cts.Token);
                progress.PagesCompleted = indexedPageUrls.Count;
                onProgress?.Invoke(progress);
            }
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Index stage fatal error");
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            // Emit audit records for every page whose chunks reached this
            // stage even on cancellation or fatal error. The embed stage
            // already upserted those chunks to the database, so the
            // Fetched -> Indexed transition is the accurate audit story.
            EmitIndexedAuditRecords(auditCtx, pageChunkCounts, pageMetadata);
        }
    }

    private static void AccumulatePageChunkCounts(DocChunk[] chunks,
                                                  HashSet<string> indexedPageUrls,
                                                  Dictionary<string, (int ChunkCount, DocCategory Category)>
                                                      pageChunkCounts,
                                                  Dictionary<string, (int Depth, string? ParentUrl)> pageMetadata)
    {
        foreach(var chunk in chunks)
        {
            indexedPageUrls.Add(chunk.PageUrl);
            if (pageChunkCounts.TryGetValue(chunk.PageUrl, out var existing))
                pageChunkCounts[chunk.PageUrl] = (existing.ChunkCount + 1, chunk.Category);
            else
                pageChunkCounts[chunk.PageUrl] = (1, chunk.Category);
            pageMetadata.TryAdd(chunk.PageUrl, (chunk.Depth, chunk.ParentUrl));
        }
    }

    private async Task RebuildIndexAsync(string? profile,
                                         ScrapeJob job,
                                         List<DocChunk> chunks,
                                         CancellationToken ct)
    {
        try
        {
            await mVectorSearch.IndexChunksAsync(profile, job.LibraryId, job.Version, chunks, ct);
            mLogger.LogInformation("Rebuilt vector index with {Count} new chunks", chunks.Count);
        }
        catch(Exception ex)
        {
            mLogger.LogWarning(ex, "Vector index rebuild failed, will retry on next batch");
        }
    }

    private void EmitIndexedAuditRecords(AuditContext auditCtx,
                                         IReadOnlyDictionary<string, (int ChunkCount, DocCategory Category)>
                                             pageChunkCounts,
                                         IReadOnlyDictionary<string, (int Depth, string? ParentUrl)> pageMetadata)
    {
        foreach((var pageUrl, (var chunkCount, var category)) in pageChunkCounts)
        {
            var host = new Uri(pageUrl).Host;
            pageMetadata.TryGetValue(pageUrl, out var meta);
            mAuditWriter.RecordIndexed(auditCtx,
                                       pageUrl,
                                       meta.ParentUrl,
                                       host,
                                       meta.Depth,
                                       new AuditPageOutcome
                                           {
                                               FetchStatus = null,
                                               Category = category.ToString(),
                                               ChunkCount = chunkCount
                                           }
                                      );
            mBroadcaster.RecordPageCompleted(auditCtx.JobId);
        }
    }
}
