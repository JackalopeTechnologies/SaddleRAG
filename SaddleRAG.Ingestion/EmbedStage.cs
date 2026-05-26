// EmbedStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Fourth stage of the streaming ingestion pipeline. Buffers chunks
///     coming off the chunk stage into <see cref="EmbedBatchSize" /> batches,
///     calls the embedding provider once per batch, upserts the embedded
///     chunks, and forwards them downstream. Per-batch embedding failures
///     are absorbed locally (skip + ErrorCount++); stage-level cancellation
///     or non-embed exceptions fault the channel and cancel the shared CTS.
///     The truncation + retry helpers are exposed as <c>internal static</c>
///     so the prompt format, the <see cref="MaxEmbedChars" /> cap, and the
///     retry-once semantics live in exactly one place and can be reused by
///     other callers without duplicating code.
/// </summary>
internal sealed class EmbedStage
{
    public EmbedStage(IEmbeddingProvider embeddingProvider,
                      IChunkRepository chunkRepository,
                      IMonitorBroadcaster broadcaster,
                      ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);
        mEmbeddingProvider = embeddingProvider;
        mChunkRepository = chunkRepository;
        mBroadcaster = broadcaster;
        mLogger = logger;
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IChunkRepository mChunkRepository;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILogger mLogger;

    /// <summary>
    ///     Run the embed stage to completion, cancellation, or fatal error.
    ///     Always completes <paramref name="output" /> in the finally block.
    /// </summary>
    public async Task RunAsync(ChannelReader<DocChunk[]> input,
                               ChannelWriter<DocChunk[]> output,
                               ScrapeJobRecord progress,
                               Action<ScrapeJobRecord>? onProgress,
                               CancellationTokenSource cts,
                               IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                               DryRunAccumulator? dryRunAcc = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cts);

        var batch = new List<DocChunk>();

        try
        {
            await foreach(var pageChunks in input.ReadAllAsync(cts.Token))
            {
                batch.AddRange(pageChunks);

                while (batch.Count >= EmbedBatchSize)
                {
                    var toEmbed = batch.Take(EmbedBatchSize).ToList();
                    batch = batch.Skip(EmbedBatchSize).ToList();
                    await EmbedAndForwardBatchAsync(toEmbed,
                                                   output,
                                                   progress,
                                                   onProgress,
                                                   cts.Token,
                                                   persistMode,
                                                   dryRunAcc
                                                  );
                }
            }

            // Flush remaining chunks
            if (batch.Count > 0)
                await EmbedAndForwardBatchAsync(batch,
                                                output,
                                                progress,
                                                onProgress,
                                                cts.Token,
                                                persistMode,
                                                dryRunAcc
                                               );
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Embed stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task EmbedAndForwardBatchAsync(List<DocChunk> batch,
                                                 ChannelWriter<DocChunk[]> output,
                                                 ScrapeJobRecord progress,
                                                 Action<ScrapeJobRecord>? onProgress,
                                                 CancellationToken ct,
                                                 IngestionPersistenceMode persistMode,
                                                 DryRunAccumulator? dryRunAcc)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var embeddedChunks = await EmbedBatchAsync(mEmbeddingProvider, mLogger, batch, ct);

            if (persistMode == IngestionPersistenceMode.Full)
                await mChunkRepository.UpsertChunksAsync(embeddedChunks, ct);

            progress.ChunksEmbedded += embeddedChunks.Length;
            onProgress?.Invoke(progress);
            for(var ei = 0; ei < embeddedChunks.Length; ei++)
                mBroadcaster.RecordChunkEmbedded(progress.Id);

            await output.WriteAsync(embeddedChunks, ct);

            long embedMs = sw.ElapsedMilliseconds;
            dryRunAcc?.RecordEmbeddedBatch(embedMs);
            mLogger.LogInformation("Embedded batch in {EmbedMs}ms count={Count}",
                                   embedMs,
                                   embeddedChunks.Length
                                  );
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            long embedMs = sw.ElapsedMilliseconds;
            mLogger.LogWarning(ex,
                               "Embedding failed for batch of {Count} chunks after {EmbedMs}ms, skipping",
                               batch.Count,
                               embedMs
                              );
            progress.IncrementErrorCount();
        }
    }

    /// <summary>
    ///     Caps the prompt at <paramref name="maxChars" /> before sending to
    ///     the embedding provider.
    /// </summary>
    internal static string TruncateForEmbedding(string text, int maxChars)
    {
        string result = text.Length > maxChars ? text[..maxChars] : text;
        return result;
    }

    /// <summary>
    ///     Embed a single batch and return chunks with the
    ///     <see cref="DocChunk.Embedding" /> field populated. Centralizes the
    ///     [Category]+[LibraryId]+[PageTitle]+Content prompt format, the
    ///     <see cref="MaxEmbedChars" /> cap, and the retry-once semantics.
    /// </summary>
    internal static async Task<DocChunk[]> EmbedBatchAsync(IEmbeddingProvider provider,
                                                           ILogger logger,
                                                           IReadOnlyList<DocChunk> chunks,
                                                           CancellationToken ct)
    {
        var texts = chunks
                    .Select(c =>
                                TruncateForEmbedding($"[{c.Category}] [{c.LibraryId}] [{c.PageTitle}]\n{c.Content}",
                                                     MaxEmbedChars
                                                    )
                           )
                    .ToList();

        float[][] embeddings = await EmbedWithRetryAsync(provider, logger, texts, ct);

        var embedded = new DocChunk[chunks.Count];
        for(var i = 0; i < chunks.Count; i++)
            embedded[i] = chunks[i] with { Embedding = embeddings[i] };

        return embedded;
    }

    /// <summary>
    ///     Retry-once helper around <see cref="IEmbeddingProvider.EmbedAsync" />.
    /// </summary>
    internal static async Task<float[][]> EmbedWithRetryAsync(IEmbeddingProvider provider,
                                                              ILogger logger,
                                                              IReadOnlyList<string> texts,
                                                              CancellationToken ct)
    {
        float[][] result;
        try
        {
            result = await provider.EmbedAsync(texts, ct: ct);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Embedding failed, retrying once");
            result = await provider.EmbedAsync(texts, ct: ct);
        }

        return result;
    }

    /// <summary>
    ///     Hard ceiling on the prompt size sent to the embedding provider.
    ///     The default nomic-embed-text model's context window is 2048
    ///     tokens; 6000 chars (~2000 tokens at ~3 chars/token) leaves headroom.
    /// </summary>
    internal const int MaxEmbedChars = 6000;

    private const int EmbedBatchSize = 32;
}
