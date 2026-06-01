// DrainStageTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     The dry-run orchestrator uses DrainStage in place of IndexStage so
///     the embed stage's bounded output channel doesn't block. Drains
///     until the writer completes, then returns. Propagates cancellation.
/// </summary>
public sealed class DrainStageTests
{
    private static DocChunk NewChunk() => new()
                                              {
                                                  Id = "c",
                                                  LibraryId = "lib",
                                                  Version = "v1",
                                                  PageUrl = "https://example.test/",
                                                  PageTitle = "t",
                                                  Category = DocCategory.HowTo,
                                                  Content = "c"
                                              };

    [Fact]
    public async Task RunAsyncDrainsUntilWriterCompletes()
    {
        var channel = Channel.CreateBounded<DocChunk[]>(2);
        await channel.Writer.WriteAsync(new[] { NewChunk() }, TestContext.Current.CancellationToken);
        await channel.Writer.WriteAsync(new[] { NewChunk(), NewChunk() }, TestContext.Current.CancellationToken);
        channel.Writer.Complete();

        var stage = new DrainStage();
        await stage.RunAsync(channel.Reader, TestContext.Current.CancellationToken);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RunAsyncPropagatesCancellation()
    {
        var channel = Channel.CreateBounded<DocChunk[]>(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var stage = new DrainStage();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage.RunAsync(channel.Reader, cts.Token));
    }

    [Fact]
    public async Task RunAsyncPropagatesCancellationMidStreamWhenWriterStillOpen()
    {
        var channel = Channel.CreateBounded<DocChunk[]>(2);
        using var cts = new CancellationTokenSource();
        await channel.Writer.WriteAsync(new[] { NewChunk() }, TestContext.Current.CancellationToken);

        var stage = new DrainStage();
        var task = stage.RunAsync(channel.Reader, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
