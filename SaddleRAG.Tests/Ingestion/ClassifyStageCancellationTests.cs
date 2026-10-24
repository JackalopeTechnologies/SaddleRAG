// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Symbols;

namespace SaddleRAG.Tests.Ingestion;

public sealed class ClassifyStageCancellationTests
{
    private static readonly TimeSpan smCompletionTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentClassifierCancellationCountsAnErrorAndContinues(bool useSharedProcessor)
    {
        using var internalCancellation = new CancellationTokenSource();
        await internalCancellation.CancelAsync();
        var classifier = Substitute.For<ILlmClassifier>();
        classifier.ClassifyAsync(Arg.Any<PageRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromException<(DocCategory Category, float Confidence)>(
                               new OperationCanceledException(internalCancellation.Token)),
                           Task.FromResult((DocCategory.HowTo, 1.0f)));
        var pages = Substitute.For<IPageRepository>();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var processor = new IngestionPageProcessor(classifier,
                                                   new CategoryAwareChunker(new SymbolExtractor()),
                                                   Substitute.For<IEmbeddingProvider>(),
                                                   Substitute.For<IVectorSearchProvider>(),
                                                   NullLogger<IngestionPageProcessor>.Instance);
        var stage = useSharedProcessor
                        ? new ClassifyStage(processor, pages, broadcaster, NullLogger.Instance)
                        : new ClassifyStage(classifier, pages, broadcaster, NullLogger.Instance);
        var job = new ScrapeJob
                      {
                          LibraryId = "lib",
                          Version = "v1",
                          RootUrl = "https://example.test/",
                          LibraryHint = "lib-hint",
                          AllowedUrlPatterns = []
                      };
        var progress = new ScrapeJobRecord { Id = "job", Job = job };
        var input = Channel.CreateBounded<PageRecord>(2);
        var output = Channel.CreateUnbounded<PageRecord>();
        await input.Writer.WriteAsync(CreatePage(), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(CreatePage() with { Id = "lib/v1/next" }, TestContext.Current.CancellationToken);
        input.Writer.Complete();
        using var cancellation = new CancellationTokenSource();

        await stage.RunAsync(job, input.Reader, output.Writer, progress, null, cancellation)
                   .WaitAsync(smCompletionTimeout, TestContext.Current.CancellationToken);

        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(1, progress.ErrorCount);
        Assert.Equal(2, progress.PagesClassified);
        Assert.True(output.Reader.TryRead(out PageRecord? first));
        Assert.NotNull(first);
        Assert.Equal(DocCategory.Unclassified, first.Category);
        Assert.True(output.Reader.TryRead(out PageRecord? second));
        Assert.NotNull(second);
        Assert.Equal(DocCategory.HowTo, second.Category);
        await output.Reader.Completion.WaitAsync(smCompletionTimeout, TestContext.Current.CancellationToken);
        await pages.Received(1).UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, IngestionPersistenceMode.Full)]
    [InlineData(true, IngestionPersistenceMode.Full)]
    [InlineData(false, IngestionPersistenceMode.DryRun)]
    [InlineData(true, IngestionPersistenceMode.DryRun)]
    public async Task CancellingActiveClassificationStopsWithoutPersistingOrCountingAnError(
        bool useSharedProcessor,
        IngestionPersistenceMode persistMode)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = Substitute.For<ILlmClassifier>();
        classifier.ClassifyAsync(Arg.Any<PageRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(async call =>
                           {
                               CancellationToken token = call.Arg<CancellationToken>();
                               entered.TrySetResult();
                               await release.Task.WaitAsync(token);
                               return (DocCategory.HowTo, 1.0f);
                           });
        var pages = Substitute.For<IPageRepository>();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var processor = new IngestionPageProcessor(classifier,
                                                   new CategoryAwareChunker(new SymbolExtractor()),
                                                   Substitute.For<IEmbeddingProvider>(),
                                                   Substitute.For<IVectorSearchProvider>(),
                                                   NullLogger<IngestionPageProcessor>.Instance);
        var stage = useSharedProcessor
                        ? new ClassifyStage(processor, pages, broadcaster, NullLogger.Instance)
                        : new ClassifyStage(classifier, pages, broadcaster, NullLogger.Instance);
        var job = new ScrapeJob
                      {
                          LibraryId = "lib",
                          Version = "v1",
                          RootUrl = "https://example.test/",
                          LibraryHint = "lib-hint",
                          AllowedUrlPatterns = []
                      };
        var progress = new ScrapeJobRecord { Id = "job", Job = job };
        var input = Channel.CreateBounded<PageRecord>(1);
        var output = Channel.CreateBounded<PageRecord>(1);
        await input.Writer.WriteAsync(CreatePage(), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        using var cancellation = new CancellationTokenSource();
        Task run = stage.RunAsync(job, input.Reader, output.Writer, progress, null, cancellation, persistMode);

        try
        {
            await entered.Task.WaitAsync(smCompletionTimeout, TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                run.WaitAsync(smCompletionTimeout, TestContext.Current.CancellationToken));

            await output.Reader.Completion.WaitAsync(smCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, progress.ErrorCount);
            Assert.Equal(0, progress.PagesClassified);
            await pages.DidNotReceive()
                       .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());
            broadcaster.DidNotReceive().RecordPageClassified(Arg.Any<string>());
        }
        finally
        {
            // Also release the fake on assertion failure so a broken cancellation path cannot hang the test run.
            release.TrySetResult();
        }
    }

    private static PageRecord CreatePage() => new()
                                                 {
                                                     Id = "lib/v1/page",
                                                     LibraryId = "lib",
                                                     Version = "v1",
                                                     Url = "https://example.test/page",
                                                     Title = "Page",
                                                     Category = DocCategory.Unclassified,
                                                     RawContent = "Documentation",
                                                     ContentHash = "hash",
                                                     FetchedAt = DateTime.UtcNow
                                                 };
}
