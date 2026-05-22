// DryRunAuditTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
///     Verifies that <see cref="IngestionOrchestrator.DryRunAsync" /> records
///     skip and fetch audit events for a minimal single-page dry run.
/// </summary>
public sealed class DryRunAuditTests
{
    private sealed class SpyAuditWriter : IScrapeAuditWriter
    {
        public List<AuditContext> FetchedCalls { get; } = [];
        public List<AuditContext> SkippedCalls { get; } = [];

        public void RecordSkipped(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth,
                                  AuditSkipReason reason,
                                  string? detail)
            => SkippedCalls.Add(ctx);

        public void RecordFetched(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth)
            => FetchedCalls.Add(ctx);

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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DryRunRecordsAuditEntriesForLibraryAndVersion()
    {
        const string LibraryId = "dryrun-test";
        const string Version = "1.0";
        const string JobId = "test-job-01";

        var auditWriter = new SpyAuditWriter();
        var pageRepo = new NullPageRepository();
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
                          RootUrl = "https://example.com/",
                          LibraryId = LibraryId,
                          Version = Version,
                          LibraryHint = "Dry run test",
                          AllowedUrlPatterns = ["example.com"],
                          ExcludedUrlPatterns = [],
                          MaxPages = 1,
                          FetchDelayMs = 0,
                          SameHostDepth = 1,
                          OffSiteDepth = 0
                      };

        await orchestrator.DryRunAsync(job,
                                       LibraryId,
                                       Version,
                                       JobId,
                                       onProgress: null,
                                       ct: TestContext.Current.CancellationToken
                                      );

        Assert.True(auditWriter.FetchedCalls.Count > 0 || auditWriter.SkippedCalls.Count > 0,
                    "DryRunAsync should have recorded at least one audit event."
                   );

        bool allMatchLibrary = auditWriter.FetchedCalls.Concat(auditWriter.SkippedCalls)
                                          .All(c => c is { LibraryId: LibraryId, Version: Version, JobId: JobId }
                                              );
        Assert.True(allMatchLibrary, "All audit calls should carry the supplied library, version, and jobId.");
    }

    private const int VectorDim = 4;
}
