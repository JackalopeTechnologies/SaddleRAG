// IngestionOrchestratorDryRunTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

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
        var broadcaster = Substitute.For<IMonitorBroadcaster>();

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
        var llmClassifier = new LlmClassifier(Options.Create(ollamaSettings),
                                              NullLogger<LlmClassifier>.Instance
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
                                                     broadcaster,
                                                     NullLogger<IngestionOrchestrator>.Instance
                                                    );

        var job = new ScrapeJob
                      {
                          LibraryId = "lib",
                          Version = "v1",
                          RootUrl = "https://example.test/",
                          LibraryHint = "lib-hint",
                          AllowedUrlPatterns = ["example.test"]
                      };

        var report = await orchestrator.DryRunAsync(job,
                                                    libraryId: "lib",
                                                    version: "v1",
                                                    jobId: "job-1",
                                                    onProgress: null,
                                                    ct: TestContext.Current.CancellationToken
                                                   );

        Assert.Equal(IngestionPersistenceMode.DryRun, crawler.LastPersistMode);
        Assert.NotNull(crawler.LastDryRunAcc);

        await pageRepo.DidNotReceiveWithAnyArgs()
                      .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());

        await chunkRepo.DidNotReceiveWithAnyArgs()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());

        await vectorSearch.DidNotReceiveWithAnyArgs()
                          .IndexChunksAsync(Arg.Any<string?>(),
                                            Arg.Any<string>(),
                                            Arg.Any<string>(),
                                            Arg.Any<IReadOnlyList<DocChunk>>(),
                                            Arg.Any<CancellationToken>()
                                           );

        await bm25ShardRepo.DidNotReceiveWithAnyArgs()
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

    private const int VectorDim = 4;
}
