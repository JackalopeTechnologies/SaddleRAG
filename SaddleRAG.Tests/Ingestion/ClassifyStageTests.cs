// ClassifyStageTests.cs
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
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies the ClassifyStage's contract with the rest of the pipeline:
///     per-page classification failures are absorbed (page passes through as
///     Unclassified, error count bumps), high-confidence classifications
///     produce an upserted PageRecord with the new Category, and stage-level
///     cancellation / fatal errors propagate the same way CrawlStage and
///     IndexStage do.
/// </summary>
public sealed class ClassifyStageTests
{
    private sealed class StubLlmClassifier : ILlmClassifier
    {
        public Func<PageRecord, string, (DocCategory Category, float Confidence)>? Behavior { get; set; }
        public List<(PageRecord Page, string Hint)> Calls { get; } = [];

        public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                            string libraryHint,
                                                                            CancellationToken ct = default)
        {
            Calls.Add((page, libraryHint));
            var result = Behavior?.Invoke(page, libraryHint) ?? (DocCategory.Unclassified, 0f);
            return Task.FromResult(result);
        }
    }

    private static ScrapeJob NewJob() => new()
                                             {
                                                 LibraryId = "lib",
                                                 Version = "v1",
                                                 RootUrl = "https://example.test/",
                                                 LibraryHint = "lib-hint",
                                                 AllowedUrlPatterns = []
                                             };

    private static ScrapeJobRecord NewProgress() => new()
                                                        {
                                                            Id = "job-1",
                                                            Job = NewJob()
                                                        };

    private static PageRecord NewPage(string url) => new()
                                                         {
                                                             Id = url,
                                                             LibraryId = "lib",
                                                             Version = "v1",
                                                             Url = url,
                                                             Title = "t",
                                                             Category = DocCategory.Unclassified,
                                                             RawContent = "c",
                                                             FetchedAt = DateTime.UtcNow,
                                                             ContentHash = "h"
                                                         };

    [Fact]
    public async Task ClassifyPageAsyncReturnsRelabeledPageOnHighConfidence()
    {
        // Direct call to the internal helper that the single-page ingest path
        // uses. Same behavior as via RunAsync: (HowTo, 0.95) -> relabel +
        // upsert, no error callback invoked.
        var classifier = new StubLlmClassifier { Behavior = (_, _) => (DocCategory.HowTo, 0.95f) };
        var pages = Substitute.For<IPageRepository>();
        var stage = new ClassifyStage(classifier, pages, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);
        var errorCount = 0;

        var result = await stage.ClassifyPageAsync(NewPage("https://example.test/p1"), "lib-hint", () => errorCount++);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(0, errorCount);
        await pages.Received(1)
                   .UpsertPageAsync(Arg.Is<PageRecord>(p => p.Category == DocCategory.HowTo),
                                    Arg.Any<CancellationToken>()
                                   );
    }

    [Fact]
    public async Task ClassifyPageAsyncWithNullOnErrorSwallowsClassifierExceptionWithoutThrowing()
    {
        // The single-page ingest path calls ClassifyPageAsync with no onError.
        // A classifier exception must still be absorbed (returns the original
        // page) without throwing NullReferenceException on the missing callback.
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => throw new InvalidOperationException("ollama-down")
                             };
        var stage = new ClassifyStage(classifier,
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      NullLogger.Instance
                                     );
        var page = NewPage("https://example.test/p1");

        var result = await stage.ClassifyPageAsync(page, "lib-hint");

        Assert.Equal(DocCategory.Unclassified, result.Category);
    }

    [Fact]
    public async Task ClassifyPageAsyncWithOnErrorFiresCallbackExactlyOnceOnClassifierException()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => throw new InvalidOperationException("ollama-down")
                             };
        var stage = new ClassifyStage(classifier,
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      NullLogger.Instance
                                     );
        var errorCount = 0;

        await stage.ClassifyPageAsync(NewPage("https://example.test/p1"), "lib-hint", () => errorCount++);

        Assert.Equal(1, errorCount);
    }

    [Fact]
    public async Task RunAsyncHighConfidenceClassificationUpsertsPageWithNewCategoryAndForwardsIt()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => (DocCategory.HowTo, 0.95f)
                             };
        var pages = Substitute.For<IPageRepository>();
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var stage = new ClassifyStage(classifier, pages, broadcaster, NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        var page = NewPage("https://example.test/p1");
        await input.Writer.WriteAsync(page, TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(NewJob(), input.Reader, output.Writer, progress, null, cts);

        var forwarded = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DocCategory.HowTo, forwarded.Category);
        await pages.Received(1)
                   .UpsertPageAsync(Arg.Is<PageRecord>(p => p.Category == DocCategory.HowTo),
                                    Arg.Any<CancellationToken>()
                                   );
        broadcaster.Received(1).RecordPageClassified("job-1");
        Assert.Equal(1, progress.PagesClassified);
    }

    [Fact]
    public async Task RunAsyncUnclassifiedResultLeavesPageUnchangedAndDoesNotUpsert()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => (DocCategory.Unclassified, 0f)
                             };
        var pages = Substitute.For<IPageRepository>();
        var stage = new ClassifyStage(classifier, pages, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        var page = NewPage("https://example.test/p1");
        await input.Writer.WriteAsync(page, TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(NewJob(), input.Reader, output.Writer, NewProgress(), null, cts);

        var forwarded = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DocCategory.Unclassified, forwarded.Category);
        Assert.Empty(pages.ReceivedCalls());
    }

    [Fact]
    public async Task RunAsyncZeroConfidenceLeavesPageUnchangedAndDoesNotUpsert()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => (DocCategory.HowTo, 0f)
                             };
        var pages = Substitute.For<IPageRepository>();
        var stage = new ClassifyStage(classifier, pages, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(NewJob(), input.Reader, output.Writer, NewProgress(), null, cts);

        var forwarded = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(DocCategory.HowTo, forwarded.Category);
        Assert.Empty(pages.ReceivedCalls());
    }

    [Fact]
    public async Task RunAsyncPassesLibraryHintFromJobToClassifier()
    {
        var classifier = new StubLlmClassifier();
        var stage = new ClassifyStage(classifier,
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      NullLogger.Instance
                                     );

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(NewJob(), input.Reader, output.Writer, NewProgress(), null, cts);

        var call = Assert.Single(classifier.Calls);
        Assert.Equal("lib-hint", call.Hint);
    }

    [Fact]
    public async Task RunAsyncClassifierExceptionPassesPageThroughAndIncrementsErrorCount()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => throw new InvalidOperationException("ollama-unreachable")
                             };
        var pages = Substitute.For<IPageRepository>();
        var stage = new ClassifyStage(classifier, pages, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(NewJob(), input.Reader, output.Writer, progress, null, cts);

        var forwarded = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DocCategory.Unclassified, forwarded.Category);
        Assert.Equal(1, progress.ErrorCount);
        Assert.Empty(pages.ReceivedCalls());
    }

    [Fact]
    public async Task RunAsyncCompletesOutputChannelWhenInputCompletes()
    {
        var stage = new ClassifyStage(new StubLlmClassifier(),
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      NullLogger.Instance
                                     );

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(NewJob(), input.Reader, output.Writer, NewProgress(), null, cts);

        await output.Reader.Completion;
    }

    [Fact]
    public async Task RunAsyncOnCancelCompletesChannelSilentlyAndRethrows()
    {
        var stage = new ClassifyStage(new StubLlmClassifier(),
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      NullLogger.Instance
                                     );

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    stage.RunAsync(NewJob(),
                                                                                   input.Reader,
                                                                                   output.Writer,
                                                                                   NewProgress(),
                                                                                   null,
                                                                                   cts
                                                                                  )
                                                               );

        await output.Reader.Completion;
    }

    [Fact]
    public async Task RunAsyncAbsorbsUpsertExceptionWithSameSemanticsAsClassifierException()
    {
        var classifier = new StubLlmClassifier
                             {
                                 Behavior = (_, _) => (DocCategory.HowTo, 0.9f)
                             };
        var pages = Substitute.For<IPageRepository>();
        pages.UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>())
             .Returns(_ => throw new InvalidOperationException("mongo-down"));
        var stage = new ClassifyStage(classifier, pages, Substitute.For<IMonitorBroadcaster>(), NullLogger.Instance);

        var input = Channel.CreateBounded<PageRecord>(4);
        var output = Channel.CreateBounded<PageRecord>(4);
        await input.Writer.WriteAsync(NewPage("https://example.test/p1"), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        var progress = NewProgress();

        await stage.RunAsync(NewJob(), input.Reader, output.Writer, progress, null, cts);

        var forwarded = await output.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DocCategory.Unclassified, forwarded.Category);
        Assert.Equal(1, progress.ErrorCount);
        Assert.False(cts.IsCancellationRequested);
    }
}
