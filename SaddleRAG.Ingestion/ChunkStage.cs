// ChunkStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Chunking;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Third stage of the streaming ingestion pipeline. Consumes classified
///     <see cref="PageRecord" /> from the classify stage, runs them through
///     <see cref="IChunker.Chunk" />, and forwards each non-empty chunk
///     array to the embed stage. Per-page chunking exceptions are logged at
///     warning and the page is skipped (no chunks emitted) with an
///     incremented error count — only stage-level cancellation or
///     non-chunker exceptions terminate the stage.
/// </summary>
internal sealed class ChunkStage
{
    public ChunkStage(IChunker chunker, IMonitorBroadcaster broadcaster, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        mChunker = chunker;
        mBroadcaster = broadcaster;
        mLogger = logger;
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IChunker mChunker;
    private readonly ILogger mLogger;

    /// <summary>
    ///     Run the chunk stage to completion, cancellation, or fatal error.
    ///     Always completes <paramref name="output" /> in the finally block.
    /// </summary>
    public async Task RunAsync(ChannelReader<PageRecord> input,
                               ChannelWriter<DocChunk[]> output,
                               ScrapeJobRecord progress,
                               Action<ScrapeJobRecord>? onProgress,
                               CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            await foreach(var page in input.ReadAllAsync(cts.Token))
                await ChunkPageAsync(page, output, progress, onProgress, cts.Token);
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Chunk stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task ChunkPageAsync(PageRecord page,
                                      ChannelWriter<DocChunk[]> output,
                                      ScrapeJobRecord progress,
                                      Action<ScrapeJobRecord>? onProgress,
                                      CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chunkCount = 0;
        try
        {
            var chunks = mChunker.Chunk(page);
            chunkCount = chunks.Count;
            if (chunks.Count > 0)
            {
                await output.WriteAsync(chunks.ToArray(), ct);
                progress.ChunksGenerated += chunks.Count;
                onProgress?.Invoke(progress);
                for(var ci = 0; ci < chunks.Count; ci++)
                    mBroadcaster.RecordChunkGenerated(progress.Id);
            }
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Chunking failed for {Url}, skipping page", page.Url);
            progress.IncrementErrorCount();
        }

        long chunkMs = sw.ElapsedMilliseconds;
        mLogger.LogInformation("Chunked {Url} in {ChunkMs}ms count={ChunkCount}",
                               page.Url,
                               chunkMs,
                               chunkCount
                              );
    }
}
