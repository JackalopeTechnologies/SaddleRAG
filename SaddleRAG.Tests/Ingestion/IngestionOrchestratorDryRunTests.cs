// IngestionOrchestratorDryRunTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Suspect;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies the dry-run entry point on <see cref="IngestionOrchestrator" />.
///     The contract under test: <c>DryRunAsync</c> drives the crawl, classify,
///     chunk, and embed stages through the same wiring as a real scrape but
///     in <see cref="IngestionPersistenceMode.DryRun" /> mode, never invokes
///     IndexStage or the finalizer, and skips every Upsert call so the run
///     produces no persisted state.
/// </summary>
public sealed class IngestionOrchestratorDryRunTests
{
    #region Test doubles

    private sealed class StubCrawler : IPageCrawler
    {
        public List<PageRecord> Pages { get; init; } = [];
        public IngestionPersistenceMode? LastPersistMode { get; private set; }
        public DryRunAccumulator? LastDryRunAcc { get; private set; }

        public async Task CrawlAsync(ScrapeJob job,
                                     ChannelWriter<PageRecord> output,
                                     string jobId = "",
                                     IReadOnlySet<string>? resumeUrls = null,
                                     IReadOnlyList<string>? seedUrls = null,
                                     Action<int>? onPageFetched = null,
                                     Action<int>? onQueued = null,
                                     Action? onFetchError = null,
                                     IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                                     DryRunAccumulator? dryRunAcc = null,
                                     CancellationToken ct = default)
        {
            LastPersistMode = persistMode;
            LastDryRunAcc = dryRunAcc;
            foreach(var page in Pages)
            {
                dryRunAcc?.RecordTotalPage(new Uri(page.Url).Host, page.Depth, inScope: true);
                dryRunAcc?.RecordFetchMs(StubFetchMs);
                await output.WriteAsync(page, ct);
                onPageFetched?.Invoke(Pages.IndexOf(page) + 1);
            }

            output.TryComplete();
        }

        public Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                     string version,
                                                     string url,
                                                     CancellationToken ct = default) =>
            Task.FromResult<PageRecord?>(null);

        private const long StubFetchMs = 1;
    }

    private sealed class CancellingStubCrawler : IPageCrawler
    {
        public CancellationTokenSource? CancelAfterEmission { get; set; }

        public async Task CrawlAsync(ScrapeJob job,
                                     ChannelWriter<PageRecord> output,
                                     string jobId = "",
                                     IReadOnlySet<string>? resumeUrls = null,
                                     IReadOnlyList<string>? seedUrls = null,
                                     Action<int>? onPageFetched = null,
                                     Action<int>? onQueued = null,
                                     Action? onFetchError = null,
                                     IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                                     DryRunAccumulator? dryRunAcc = null,
                                     CancellationToken ct = default)
        {
            CancelAfterEmission?.Cancel();
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            output.TryComplete();
        }

        public Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                     string version,
                                                     string url,
                                                     CancellationToken ct = default) =>
            Task.FromResult<PageRecord?>(null);
    }

    private sealed class ThrowingStubCrawler : IPageCrawler
    {
        public required Exception Error { get; init; }

        public Task CrawlAsync(ScrapeJob job,
                               ChannelWriter<PageRecord> output,
                               string jobId = "",
                               IReadOnlySet<string>? resumeUrls = null,
                               IReadOnlyList<string>? seedUrls = null,
                               Action<int>? onPageFetched = null,
                               Action<int>? onQueued = null,
                               Action? onFetchError = null,
                               IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                               DryRunAccumulator? dryRunAcc = null,
                               CancellationToken ct = default)
        {
            output.TryComplete(Error);
            throw Error;
        }

        public Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                                     string version,
                                                     string url,
                                                     CancellationToken ct = default) =>
            Task.FromResult<PageRecord?>(null);
    }

    private sealed class TestHarness
    {
        public required IngestionOrchestrator Orchestrator { get; init; }
        public required IPageRepository PageRepo { get; init; }
        public required IChunkRepository ChunkRepo { get; init; }
        public required IVectorSearchProvider VectorSearch { get; init; }
        public required IBm25ShardRepository Bm25ShardRepo { get; init; }
        public required IMonitorBroadcaster Broadcaster { get; init; }
    }

    #endregion

    #region Helpers

    private static TestHarness BuildOrchestrator(IPageCrawler crawler, IMonitorBroadcaster? broadcaster = null)
    {
        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));

        var chunkRepo = Substitute.For<IChunkRepository>();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var libraryProfileRepo = Substitute.For<ILibraryProfileRepository>();
        var libraryIndexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var auditWriter = Substitute.For<IScrapeAuditWriter>();
        var resolvedBroadcaster = broadcaster ?? Substitute.For<IMonitorBroadcaster>();

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(),
                                     Arg.Any<EmbedRole>(),
                                     Arg.Any<CancellationToken>())
                         .Returns(call =>
                                  {
                                      var texts = call.Arg<IReadOnlyList<string>>();
                                      var result = new float[texts.Count][];
                                      for(var i = 0; i < texts.Count; i++)
                                          result[i] = new float[VectorDim];
                                      return Task.FromResult(result);
                                  }
                                 );

        var ollamaSettings = new OllamaSettings();
        ollamaSettings.ClassificationModels.Add(new OllamaModelEntry { Name = "test-classifier:latest" });
        var llmClassifier = new OllamaLlmClassifier(Options.Create(ollamaSettings),
                                              NullLogger<OllamaLlmClassifier>.Instance
                                             );

        var symbolExtractor = new SymbolExtractor();
        var chunker = new CategoryAwareChunker(symbolExtractor);
        var suspectDetector = new SuspectDetector();

        var orchestrator = new IngestionOrchestrator(crawler,
                                                     llmClassifier,
                                                     chunker,
                                                     embeddingProvider,
                                                     vectorSearch,
                                                     libraryRepo,
                                                     pageRepo,
                                                     chunkRepo,
                                                     libraryProfileRepo,
                                                     libraryIndexRepo,
                                                     bm25ShardRepo,
                                                     suspectDetector,
                                                     auditWriter,
                                                     resolvedBroadcaster,
                                                     NullLogger<IngestionOrchestrator>.Instance
                                                    );

        return new TestHarness
                   {
                       Orchestrator = orchestrator,
                       PageRepo = pageRepo,
                       ChunkRepo = chunkRepo,
                       VectorSearch = vectorSearch,
                       Bm25ShardRepo = bm25ShardRepo,
                       Broadcaster = resolvedBroadcaster
                   };
    }

    private static ScrapeJob NewJob() => new()
                                             {
                                                 LibraryId = "lib",
                                                 Version = "v1",
                                                 RootUrl = "https://example.test/",
                                                 LibraryHint = "lib-hint",
                                                 AllowedUrlPatterns = ["example.test"]
                                             };

    #endregion

    #region Original test

    [Fact]
    public async Task DryRunAsyncSkipsUpsertsAndDoesNotIndexOrFinalize()
    {
        var page = new PageRecord
                       {
                           Id = "p1",
                           LibraryId = "lib",
                           Version = "v1",
                           Url = "https://example.test/p1",
                           Title = "Test Page",
                           Category = DocCategory.Unclassified,
                           RawContent = "Heading one paragraph of content sufficient for chunking. " +
                                        "More text follows to make sure the chunker emits at least one " +
                                        "non-empty chunk for the embed stage to consume.",
                           FetchedAt = DateTime.UtcNow,
                           ContentHash = "hash-1"
                       };

        var crawler = new StubCrawler { Pages = [page] };
        var harness = BuildOrchestrator(crawler);

        var report = await harness.Orchestrator.DryRunAsync(NewJob(),
                                                            libraryId: "lib",
                                                            version: "v1",
                                                            jobId: "job-1",
                                                            onProgress: null,
                                                            ct: TestContext.Current.CancellationToken
                                                           );

        Assert.Equal(IngestionPersistenceMode.DryRun, crawler.LastPersistMode);
        Assert.NotNull(crawler.LastDryRunAcc);

        await harness.PageRepo.DidNotReceiveWithAnyArgs()
                     .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());

        await harness.ChunkRepo.DidNotReceiveWithAnyArgs()
                     .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());

        await harness.VectorSearch.DidNotReceiveWithAnyArgs()
                     .IndexChunksAsync(Arg.Any<string?>(),
                                       Arg.Any<string>(),
                                       Arg.Any<string>(),
                                       Arg.Any<IReadOnlyList<DocChunk>>(),
                                       Arg.Any<CancellationToken>()
                                      );

        await harness.Bm25ShardRepo.DidNotReceiveWithAnyArgs()
                     .ReplaceShardsAsync(Arg.Any<string>(),
                                         Arg.Any<string>(),
                                         Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                         Arg.Any<CancellationToken>()
                                        );

        Assert.NotNull(report);
        Assert.NotNull(report.CategoryHistogram);
        Assert.NotNull(report.StageTimings);
        Assert.Equal(1, report.TotalPages);
        Assert.Equal(1, report.InScopePages);
    }

    #endregion

    #region Argument-validation tests

    [Fact]
    public async Task DryRunAsyncThrowsOnNullJob()
    {
        var harness = BuildOrchestrator(new StubCrawler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Orchestrator.DryRunAsync(
            NullRef<ScrapeJob>(), "lib", "v1", "job", null, TestContext.Current.CancellationToken));
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }

    [Fact]
    public async Task DryRunAsyncThrowsOnEmptyLibraryId()
    {
        var job = NewJob();
        var harness = BuildOrchestrator(new StubCrawler());
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Orchestrator.DryRunAsync(
            job, "", "v1", "job", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DryRunAsyncThrowsOnEmptyVersion()
    {
        var job = NewJob();
        var harness = BuildOrchestrator(new StubCrawler());
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Orchestrator.DryRunAsync(
            job, "lib", "", "job", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DryRunAsyncThrowsOnEmptyJobId()
    {
        var job = NewJob();
        var harness = BuildOrchestrator(new StubCrawler());
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Orchestrator.DryRunAsync(
            job, "lib", "v1", "", null, TestContext.Current.CancellationToken));
    }

    #endregion

    #region Empty-crawl and limit tests

    [Fact]
    public async Task DryRunAsyncWithEmptyCrawlReturnsZeroTotalAndEmptyHistogram()
    {
        var harness = BuildOrchestrator(new StubCrawler());

        var report = await harness.Orchestrator.DryRunAsync(NewJob(),
                                                            "lib",
                                                            "v1",
                                                            "job",
                                                            onProgress: null,
                                                            ct: TestContext.Current.CancellationToken
                                                           );

        Assert.Equal(0, report.TotalPages);
        Assert.Empty(report.CategoryHistogram);
        Assert.Equal(0, report.StageTimings.FetchSampleCount);
        Assert.Equal(0, report.StageTimings.ClassifySampleCount);
        Assert.Equal(0, report.StageTimings.ChunkSampleCount);
        Assert.Equal(0, report.StageTimings.EmbedBatchCount);
    }

    [Fact]
    public async Task DryRunAsyncHitMaxPagesLimitIsTrueWhenTotalReachesCap()
    {
        var pages = Enumerable.Range(0, 3)
                              .Select(i => new PageRecord
                                               {
                                                   Id = $"p{i}",
                                                   LibraryId = "lib",
                                                   Version = "v1",
                                                   Url = $"https://example.test/p{i}",
                                                   Title = "t",
                                                   Category = DocCategory.Unclassified,
                                                   RawContent = "content",
                                                   FetchedAt = DateTime.UtcNow,
                                                   ContentHash = "h"
                                               })
                              .ToList();
        var harness = BuildOrchestrator(new StubCrawler { Pages = pages });

        var job = NewJob() with { MaxPages = 3 };

        var report = await harness.Orchestrator.DryRunAsync(job,
                                                            "lib",
                                                            "v1",
                                                            "job",
                                                            onProgress: null,
                                                            ct: TestContext.Current.CancellationToken
                                                           );

        Assert.True(report.HitMaxPagesLimit);
    }

    #endregion

    #region Broadcaster lifecycle tests

    [Fact]
    public async Task DryRunAsyncEmitsRecordJobStartedAndRecordJobCompletedOnSuccess()
    {
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var harness = BuildOrchestrator(new StubCrawler(), broadcaster);

        await harness.Orchestrator.DryRunAsync(NewJob(),
                                               "lib",
                                               "v1",
                                               "job-1",
                                               onProgress: null,
                                               ct: TestContext.Current.CancellationToken
                                              );

        broadcaster.Received(1).RecordJobStarted("job-1", "lib", "v1", Arg.Any<string>());
        broadcaster.Received(1).RecordJobCompleted("job-1", Arg.Any<int>());
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
        broadcaster.DidNotReceive().RecordJobCancelled(Arg.Any<string>());
    }

    [Fact]
    public async Task DryRunAsyncEmitsRecordJobCancelledWhenCancelled()
    {
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var crawler = new CancellingStubCrawler();
        var harness = BuildOrchestrator(crawler, broadcaster);

        using var cts = new CancellationTokenSource();
        crawler.CancelAfterEmission = cts;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Orchestrator.DryRunAsync(NewJob(),
                                             "lib",
                                             "v1",
                                             "job-cancel",
                                             onProgress: null,
                                             ct: cts.Token
                                            ));

        broadcaster.Received(1).RecordJobCancelled("job-cancel");
        broadcaster.DidNotReceive().RecordJobFailed(Arg.Any<string>(), Arg.Any<string>());
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task DryRunAsyncEmitsRecordJobFailedWhenCrawlerThrows()
    {
        var broadcaster = Substitute.For<IMonitorBroadcaster>();
        var crawler = new ThrowingStubCrawler { Error = new InvalidOperationException("crawl-died") };
        var harness = BuildOrchestrator(crawler, broadcaster);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Orchestrator.DryRunAsync(NewJob(),
                                             "lib",
                                             "v1",
                                             "job-fail",
                                             onProgress: null,
                                             ct: TestContext.Current.CancellationToken
                                            ));

        broadcaster.Received(1).RecordJobFailed("job-fail", "crawl-died");
        broadcaster.DidNotReceive().RecordJobCompleted(Arg.Any<string>(), Arg.Any<int>());
    }

    #endregion

    private const int VectorDim = 4;
}
