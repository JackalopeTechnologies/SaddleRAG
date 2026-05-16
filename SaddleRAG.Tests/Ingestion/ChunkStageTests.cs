// ChunkStageTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies ChunkStage's contract: zero-chunk pages emit nothing
///     downstream, non-zero-chunk pages emit a single array to the channel,
///     chunker exceptions are absorbed per-page (skip + ErrorCount++) and
///     don't terminate the stage, and stage-level cancellation / fatal
///     errors propagate identically to the other stages.
/// </summary>
public sealed class ChunkStageTests
{
    private sealed class StubChunker : IChunker
    {
        public Func<PageRecord, IReadOnlyList<DocChunk>>? Behavior { get; set; }
        public List<PageRecord> Calls { get; } = [];

        public IReadOnlyList<DocChunk> Chunk(PageRecord page, LibraryProfile? libraryProfile = null)
        {
            Calls.Add(page);
            return Behavior?.Invoke(page) ?? [];
        }
    }

    private static PageRecord NewPage(string url) => new()
                                                         {
                                                             Id = url,
                                                             LibraryId = "lib",
                                                             Version = "v1",
                                                             Url = url,
                                                             Title = "t",
                                                             Category = DocCategory.HowTo,
                                                             RawContent = "c",
                                                             FetchedAt = DateTime.UtcNow,
                                                             ContentHash = "h"
                                                         };

    private static DocChunk NewChunk(string pageUrl, int index) => new()
                                                                       {
                                                                           Id = $"{pageUrl}#{index}",
                                                                           LibraryId = "lib",
                                                                           Version = "v1",
                                                                           PageUrl = pageUrl,
                                                                           PageTitle = "t",
                                                                           Category = DocCategory.HowTo,
                                                                           Content = $"chunk-{index}"
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

    [Fact]
    public async Task RunAsyncMultiChunkPageWritesSingleArrayAndFiresBroadcasterPerChunk()
    {
        var chunker = new StubChunker
                          {
                              Behavior = p => new[]
                                                  {
                                                      NewChunk(p.Url, 0),
                                                      NewChunk(p.Url, 1),
                                                      NewChunk(p.Url, 2)
                                                  }
                          };
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var stage = new ChunkStage(chunker, broadcaster, NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        var batch = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, batch.Length);
        Assert.Equal(3, progress.ChunksGenerated);
        broadcaster.Received(3).RecordChunkGenerated("job-1");
    }

    [Fact]
    public async Task RunAsyncZeroChunkPageEmitsNothingDownstreamAndDoesNotBroadcast()
    {
        var chunker = new StubChunker
                          {
                              Behavior = _ => []
                          };
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var stage = new ChunkStage(chunker, broadcaster, NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        Assert.False(output.Reader.TryRead(out _));
        Assert.Equal(0, progress.ChunksGenerated);
        Assert.Empty(broadcaster.ReceivedCalls());
    }

    [Fact]
    public async Task RunAsyncFiresOnProgressOnceWithFinalChunksGeneratedAfterMultiChunkPage()
    {
        var chunker = new StubChunker
                          {
                              Behavior = p => new[] { NewChunk(p.Url, 0), NewChunk(p.Url, 1) }
                          };
        var stage = new ChunkStage(chunker, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();
        var snapshots = new List<int>();

        await stage.RunAsync(input.Reader,
                             output.Writer,
                             progress,
                             p => snapshots.Add(p.ChunksGenerated),
                             cts
                            );

        await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(2, snapshot);
    }

    [Fact]
    public async Task RunAsyncChunkerExceptionIsAbsorbedSkipsPageAndIncrementsErrorCount()
    {
        var chunker = new StubChunker
                          {
                              Behavior = _ => throw new InvalidOperationException("bad-html")
                          };
        var stage = new ChunkStage(chunker, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        Assert.False(output.Reader.TryRead(out _));
        Assert.Equal(1, progress.ErrorCount);
        Assert.Equal(0, progress.ChunksGenerated);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task RunAsyncChunkerThrowOnFirstPageDoesNotStopProcessingSecondPage()
    {
        var firstCall = true;
        var chunker = new StubChunker
                          {
                              Behavior = p =>
                                         {
                                             if (firstCall)
                                             {
                                                 firstCall = false;
                                                 throw new InvalidOperationException("bad-page");
                                             }

                                             return new[] { NewChunk(p.Url, 0) };
                                         }
                          };
        var stage = new ChunkStage(chunker, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(NewPage("https://example.test/p2"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(input.Reader, output.Writer, progress, null, cts);

        var batch = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Single(batch);
        Assert.Equal(1, progress.ErrorCount);
        Assert.Equal(1, progress.ChunksGenerated);
    }

    [Fact]
    public async Task RunAsyncCompletesOutputChannelWhenInputCompletes()
    {
        var stage = new ChunkStage(new StubChunker(),
                                   Substitute.For<IMonitorBroadcaster>(),
                                   NullLogger.Instance
                                  );

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        await output.Reader.Completion;
    }

    [Fact]
    public async Task RunAsyncOnCancelCompletesChannelSilentlyAndRethrows()
    {
        var stage = new ChunkStage(new StubChunker(),
                                   Substitute.For<IMonitorBroadcaster>(),
                                   NullLogger.Instance
                                  );

        var input = Channel.CreateBounded<PageRecord>(4);
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
}
