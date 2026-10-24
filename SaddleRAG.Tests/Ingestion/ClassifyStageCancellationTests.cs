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
