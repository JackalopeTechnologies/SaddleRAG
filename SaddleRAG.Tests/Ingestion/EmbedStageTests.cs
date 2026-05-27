// EmbedStageTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies EmbedStage's contract: batches at EmbedBatchSize, flushes the
///     residual on input close, upserts before forwarding (so a downstream
///     index-stage crash doesn't lose chunks Mongo never saw), absorbs
///     per-batch embedding exceptions (skip + ErrorCount++), and propagates
///     stage-level cancellation / fatal errors. Also exercises the
///     internal-static helpers used by the single-page ingest path:
///     TruncateForEmbedding caps long strings; EmbedWithRetryAsync retries
///     a single failure then surfaces the second one.
/// </summary>
public sealed class EmbedStageTests
{
    private static DocChunk NewChunk(string id) => new()
                                                       {
                                                           Id = id,
                                                           LibraryId = "lib",
                                                           Version = "v1",
                                                           PageUrl = $"https://example.test/{id}",
                                                           PageTitle = "t",
                                                           Category = DocCategory.HowTo,
                                                           Content = $"content-{id}"
                                                       };

    private static ScrapeJobRecord NewProgress() => new()
                                                        {
                                                            Id = "job-1",
                                                            Job = new ScrapeJob
                                                                      {
                                                                          LibraryId = "lib",
                                                                          Version = "v1",
                                                                          RootUrl = "https://example.test/",
                                                                          LibraryHint = "lib",
                                                                          AllowedUrlPatterns = []
                                                                      }
                                                        };

    private static IEmbeddingProvider ProviderReturning(int dimensions = 4)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             var texts = call.Arg<IReadOnlyList<string>>();
                             var result = new float[texts.Count][];
                             for(var i = 0; i < texts.Count; i++)
                                 result[i] = new float[dimensions];
                             return Task.FromResult(result);
                         }
                        );
        return provider;
    }

    [Fact]
    public async Task RunAsyncResidualBatchUnderThresholdIsFlushedOnInputClose()
    {
        var provider = ProviderReturning();
        var chunks = Substitute.For<IChunkRepository>();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var stage = new EmbedStage(provider, chunks, broadcaster, NullLogger.Instance);

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(new[] { NewChunk("a"), NewChunk("b"), NewChunk("c") },
                                      TestContext.Current.CancellationToken
                                     );
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        var batch = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, batch.Length);
        Assert.All(batch, c => Assert.NotNull(c.Embedding));
        Assert.Equal(3, progress.ChunksEmbedded);
        broadcaster.Received(3).RecordChunkEmbedded("job-1");
        await chunks.Received(1)
                    .UpsertChunksAsync(Arg.Is<DocChunk[]>(a => a.Length == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsyncEmitsBatchAtThresholdThenFlushesResidualAsSeparateBatch()
    {
        var provider = ProviderReturning();
        var chunks = Substitute.For<IChunkRepository>();
        var stage = new EmbedStage(provider, chunks, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        // 33 chunks: first 32 emit as one batch at threshold, residual 1 flushes at end
        var page = new DocChunk[33];
        for(var i = 0; i < 33; i++)
            page[i] = NewChunk($"c{i}");
        await input.Writer.WriteAsync(page, TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        var first = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(32, first.Length);
        Assert.Single(second);
        await chunks.Received(2).UpsertChunksAsync(Arg.Any<DocChunk[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsyncUpsertHappensBeforeOutputWrite()
    {
        var provider = ProviderReturning();
        var chunks = Substitute.For<IChunkRepository>();
        var seenUpsertBeforeWrite = false;
        var upsertCalled = false;
        chunks.UpsertChunksAsync(Arg.Any<DocChunk[]>(), Arg.Any<CancellationToken>())
              .Returns(_ =>
                       {
                           upsertCalled = true;
                           return Task.CompletedTask;
                       }
                      );

        var stage = new EmbedStage(provider, chunks, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<DocChunk[]>(4);
        // Use a SingleReader bounded(1) so the read happens synchronously
        var output = Channel.CreateBounded<DocChunk[]>(new BoundedChannelOptions(1));
        await input.Writer.WriteAsync(new[] { NewChunk("a") }, TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var runTask = stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        var batch = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        seenUpsertBeforeWrite = upsertCalled;
        await runTask;

        Assert.True(seenUpsertBeforeWrite);
        Assert.Single(batch);
    }

    [Fact]
    public async Task RunAsyncEmbeddingProviderExceptionIsAbsorbedSkipsBatchAndIncrementsErrorCount()
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns<float[][]>(_ => throw new InvalidOperationException("ollama-down"));
        var chunks = Substitute.For<IChunkRepository>();
        var stage = new EmbedStage(provider, chunks, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(new[] { NewChunk("a") }, TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        Assert.False(output.Reader.TryRead(out _));
        Assert.Equal(1, progress.ErrorCount);
        Assert.Equal(0, progress.ChunksEmbedded);
        Assert.False(cts.IsCancellationRequested);
        await chunks.DidNotReceiveWithAnyArgs().UpsertChunksAsync(Arg.Any<DocChunk[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsyncCompletesOutputChannelWhenInputCompletes()
    {
        var stage = new EmbedStage(ProviderReturning(),
                                   Substitute.For<IChunkRepository>(),
                                   Substitute.For<IMonitorBroadcaster>(),
                                   NullLogger.Instance
                                  );

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        await output.Reader.Completion;
    }

    [Fact]
    public async Task RunAsyncOnCancelCompletesChannelSilentlyAndRethrows()
    {
        var stage = new EmbedStage(ProviderReturning(),
                                   Substitute.For<IChunkRepository>(),
                                   Substitute.For<IMonitorBroadcaster>(),
                                   NullLogger.Instance
                                  );

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    stage.RunAsync(input.Reader,
                                                                                   output.Writer,
                                                                                   NewProgress(),
                                                                                   null,
                                                                                   cts
                                                                                  )
                                                               );

        await output.Reader.Completion;
    }

    [Fact]
    public void TruncateForEmbeddingShortensTextLongerThanMaxCharsAndLeavesShortTextAlone()
    {
        var longText = new string('x', EmbedStage.MaxEmbedChars + 100);
        Assert.Equal(EmbedStage.MaxEmbedChars, EmbedStage.TruncateForEmbedding(longText, EmbedStage.MaxEmbedChars).Length);

        var shortText = "hello";
        Assert.Equal("hello", EmbedStage.TruncateForEmbedding(shortText, EmbedStage.MaxEmbedChars));
    }

    [Fact]
    public async Task EmbedWithRetryAsyncRetriesOnceOnFirstFailureThenReturnsResult()
    {
        var callCount = 0;
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                         {
                             callCount++;
                             if (callCount == 1)
                                 throw new InvalidOperationException("transient");
                             return Task.FromResult(new[] { new float[4] });
                         }
                        );

        var result = await EmbedStage.EmbedWithRetryAsync(provider,
                                                          NullLogger.Instance,
                                                          new[] { "text" },
                                                          TestContext.Current.CancellationToken
                                                         );

        Assert.Equal(2, callCount);
        Assert.Single(result);
    }

    [Fact]
    public async Task EmbedBatchAsyncReturnsChunksWithEmbeddingsAttached()
    {
        // Direct call to the helper that both the streaming batch loop and the
        // orchestrator's single-page ingest path go through. No upsert, no
        // broadcaster — those are RunAsync's job. Just texts -> embeddings ->
        // chunks-with-Embedding-set.
        var provider = ProviderReturning(dimensions: 8);
        var chunks = new[] { NewChunk("a"), NewChunk("b"), NewChunk("c") };

        var result = await EmbedStage.EmbedBatchAsync(provider,
                                                      NullLogger.Instance,
                                                      chunks,
                                                      TestContext.Current.CancellationToken
                                                     );

        Assert.Equal(3, result.Length);
        Assert.All(result,
                   c =>
                   {
                       Assert.NotNull(c.Embedding);
                       Assert.Equal(8, c.Embedding.Length);
                   }
                  );
    }

    [Fact]
    public async Task EmbedBatchAsyncCapsPromptTextAtMaxEmbedChars()
    {
        // Generate a chunk with content larger than MaxEmbedChars so the
        // truncation inside EmbedBatchAsync is exercised. The provider
        // captures the text it actually receives; we assert the cap holds.
        IReadOnlyList<string>? captured = null;
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             captured = call.Arg<IReadOnlyList<string>>();
                             return Task.FromResult(new[] { new float[4] });
                         }
                        );
        var bigChunk = NewChunk("big") with { Content = new string('x', EmbedStage.MaxEmbedChars * 2) };

        await EmbedStage.EmbedBatchAsync(provider,
                                         NullLogger.Instance,
                                         new[] { bigChunk },
                                         TestContext.Current.CancellationToken
                                        );

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.True(captured[0].Length <= EmbedStage.MaxEmbedChars);
    }

    [Fact]
    public async Task EmbedWithRetryAsyncSurfacesSecondFailureWhenBothCallsThrow()
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns<float[][]>(_ => throw new InvalidOperationException("permanent"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => EmbedStage.EmbedWithRetryAsync(provider,
                                                                       NullLogger.Instance,
                                                                       new[] { "text" },
                                                                       TestContext.Current.CancellationToken
                                                                      )
                                                           );
    }
}
