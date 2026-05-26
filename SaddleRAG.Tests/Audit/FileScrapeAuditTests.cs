// FileScrapeAuditTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Suspect;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that <see cref="IngestionOrchestrator.DryRunAsync" /> correctly
///     crawls a local file:// documentation tree, records consistent audit host
///     strings, and skips off-site links without attempting to fetch them.
/// </summary>
public sealed class FileScrapeAuditTests
{
    #region DryRunOverFileUrlRecordsFetchEventsForReachablePages test

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DryRunOverFileUrlRecordsFetchEventsForReachablePages()
    {
        const string LibraryId = "file-scrape-test";
        const string Version = "1.0";
        const string JobId = "test-job-file";

        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "FileScrape");
        Assert.True(Directory.Exists(fixtureRoot),
                    $"Test fixture directory must be copied to output. Looked for: {fixtureRoot}"
                   );

        var rootUrl = new Uri(Path.Combine(fixtureRoot, "index.htm")).AbsoluteUri;

        var auditWriter = new SpyAuditWriter();
        var pageRepo = new NullPageRepository();
        var orchestrator = BuildOrchestrator(pageRepo, auditWriter);

        // AllowedUrlPatterns = [""] — empty-string regex matches every URL, which is the
        // same default the MCP tool applies when the root host is empty (file:// scheme).
        // ExcludedUrlPatterns = [] and OffSiteDepth = 0 together ensure the external
        // https://example.com/external link is skipped without a real network fetch.
        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = LibraryId,
                          Version = Version,
                          LibraryHint = "file:// scrape integration test",
                          AllowedUrlPatterns = [""],
                          ExcludedUrlPatterns = [],
                          MaxPages = 0,
                          FetchDelayMs = 0,
                          SameHostDepth = 5,
                          OffSiteDepth = 0
                      };

        await orchestrator.DryRunAsync(job,
                                       LibraryId,
                                       Version,
                                       JobId,
                                       onProgress: null,
                                       ct: TestContext.Current.CancellationToken
                                      );

        // All 4 local .htm files must be fetched.
        Assert.Equal(expected: 4, auditWriter.FetchedCalls.Count);

        // The off-site https link must be skipped (depth-exceeded or pattern-based).
        Assert.Contains(auditWriter.SkippedCalls,
                        c => c.Reason == AuditSkipReason.OffSiteDepth || c.Reason == AuditSkipReason.PatternMissAllowed
                       );

        // All fetched audit events must carry the same host string — the fix for the
        // dryrun vs. live path inconsistency.  For file:// URLs the canonical host is
        // "" (empty string), matching what SafeGetHost and the live-crawl path produce.
        var distinctHosts = auditWriter.FetchedCalls.Select(c => c.Host).Distinct().ToList();
        Assert.Single(distinctHosts);
    }

    #endregion

    #region DryRunOverFileUrlDoesNotCallUpsertPageAsync test

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DryRunOverFileUrlDoesNotCallUpsertPageAsync()
    {
        const string LibraryId = "dryrun-persist-gate-test";
        const string Version = "1.0";
        const string JobId = "test-job-persist-gate";

        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "FileScrape");
        Assert.True(Directory.Exists(fixtureRoot),
                    $"Test fixture directory must be copied to output. Looked for: {fixtureRoot}"
                   );

        var rootUrl = new Uri(Path.Combine(fixtureRoot, "index.htm")).AbsoluteUri;

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));
        pageRepo.GetPageByUrlAsync(Arg.Any<string>(),
                                   Arg.Any<string>(),
                                   Arg.Any<string>(),
                                   Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<PageRecord?>(result: null));

        var auditWriter = new SpyAuditWriter();
        var orchestrator = BuildOrchestrator(pageRepo, auditWriter);

        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = LibraryId,
                          Version = Version,
                          LibraryHint = "dry-run persistence-mode gating integration test",
                          AllowedUrlPatterns = [""],
                          ExcludedUrlPatterns = [],
                          MaxPages = 0,
                          FetchDelayMs = 0,
                          SameHostDepth = 5,
                          OffSiteDepth = 0
                      };

        await orchestrator.DryRunAsync(job,
                                       LibraryId,
                                       Version,
                                       JobId,
                                       onProgress: null,
                                       ct: TestContext.Current.CancellationToken
                                      );

        // Sanity check: the real crawl actually fetched pages, so the gate is being
        // exercised — without this assertion a future no-op crawl would silently
        // satisfy DidNotReceive and the regression check would lose its teeth.
        Assert.True(auditWriter.FetchedCalls.Count > 0,
                    $"Expected the dry-run crawler to fetch at least one page; got {auditWriter.FetchedCalls.Count}."
                   );

        // The core contract: dry-run mode must never persist pages, no matter
        // how many the crawler fetches. The PersistMode == Full gate inside
        // PageCrawler is the single guarantor of this — drop it and this fails.
        await pageRepo.DidNotReceiveWithAnyArgs()
                      .UpsertPageAsync(Arg.Any<PageRecord>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Orchestrator helper

    private const int VectorDim = 4;

    private static IngestionOrchestrator BuildOrchestrator(IPageRepository pageRepo, SpyAuditWriter auditWriter)
    {
        var gitHubScraper = new GitHubRepoScraper(pageRepo, NullLogger<GitHubRepoScraper>.Instance);
        var broadcaster = new NullMonitorBroadcaster();
        var crawler = new PageCrawler(pageRepo,
                                      gitHubScraper,
                                      auditWriter,
                                      broadcaster,
                                      NullLogger<PageCrawler>.Instance,
                                      NullLoggerFactory.Instance
                                     );

        var chunkRepo = Substitute.For<IChunkRepository>();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var libraryProfileRepo = Substitute.For<ILibraryProfileRepository>();
        var libraryIndexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(),
                                     Arg.Any<EmbedRole>(),
                                     Arg.Any<CancellationToken>())
                         .Returns(call =>
                                  {
                                      var texts = call.Arg<IReadOnlyList<string>>();
                                      var emb = new float[texts.Count][];
                                      for(var i = 0; i < texts.Count; i++)
                                          emb[i] = new float[VectorDim];
                                      return Task.FromResult(emb);
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

        return new IngestionOrchestrator(crawler,
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
    }

    #endregion

    #region Spy types

    private sealed record AuditCall(AuditContext Context, string Url, string Host, AuditSkipReason? Reason);

    private sealed class NullMonitorBroadcaster : IMonitorBroadcaster
    {
        public void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl)
        {
        }

        public void RecordFetch(string jobId, string url)
        {
        }

        public void RecordReject(string jobId, string url, string reason)
        {
        }

        public void RecordError(string jobId, string message, string? url = null)
        {
        }

        public void RecordPageClassified(string jobId)
        {
        }

        public void RecordChunkGenerated(string jobId)
        {
        }

        public void RecordChunkEmbedded(string jobId)
        {
        }

        public void RecordPageCompleted(string jobId)
        {
        }

        public void RecordJobCompleted(string jobId, int indexedPageCount)
        {
        }

        public void RecordJobFailed(string jobId, string errorMessage)
        {
        }

        public void RecordJobCancelled(string jobId)
        {
        }

        public void RecordJobProgress(string jobId, int processed, int total, string label)
        {
        }

        public void RecordSuspectFlag(string jobId, string libraryId, string version, IReadOnlyList<string> reasons)
        {
        }

        public JobTickSnapshot? GetJobSnapshot(string jobId) => null;
        public IReadOnlyList<string> GetActiveJobIds() => [];

        public void Subscribe(string jobId, Func<JobTickEvent, Task> handler)
        {
        }

        public void Unsubscribe(string jobId, Func<JobTickEvent, Task> handler)
        {
        }

        public void BroadcastTick(string jobId)
        {
        }
    }

    private sealed class SpyAuditWriter : IScrapeAuditWriter
    {
        public List<AuditCall> FetchedCalls { get; } = [];
        public List<AuditCall> SkippedCalls { get; } = [];

        public void RecordSkipped(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth,
                                  AuditSkipReason reason,
                                  string? detail)
            => SkippedCalls.Add(new AuditCall(ctx, url, host, reason));

        public void RecordFetched(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth)
            => FetchedCalls.Add(new AuditCall(ctx, url, host, Reason: null));

        public void RecordFailed(AuditContext ctx,
                                 string url,
                                 string? parentUrl,
                                 string host,
                                 int depth,
                                 string error)
        {
        }

        public void RecordIndexed(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth,
                                  AuditPageOutcome outcome)
        {
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPageRepository : IPageRepository
    {
        public Task UpsertPageAsync(PageRecord record, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PageRecord>> GetPagesAsync(string libraryId,
                                                             string version,
                                                             CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PageRecord>>([]);

        public Task<PageRecord?> GetPageByUrlAsync(string libraryId,
                                                   string version,
                                                   string url,
                                                   CancellationToken ct = default)
            => Task.FromResult<PageRecord?>(result: null);

        public Task<int> GetPageCountAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(result: 0);

        public Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(result: 0L);

        public Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LibraryVersionKey>>([]);
    }

    #endregion
}
