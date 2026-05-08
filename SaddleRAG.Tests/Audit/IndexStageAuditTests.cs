// IndexStageAuditTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that the index stage emits the Fetched -> Indexed audit
///     transition for every page whose chunks were committed, even when
///     the pipeline is cancelled mid-stream.
/// </summary>
public sealed class IndexStageAuditTests
{
    private const int RebuildThresholdChunkCount = 100;

    [Fact]
    public async Task EmitsRecordIndexedForPagesAccumulatedBeforeCancellation()
    {
        var auditWriter = new SpyAuditWriter();
        var broadcaster = new SilentMonitorBroadcaster();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        using var cts = new CancellationTokenSource();

        vectorSearch
            .When(v => v.IndexChunksAsync(Arg.Any<string?>(),
                                          Arg.Any<string>(),
                                          Arg.Any<string>(),
                                          Arg.Any<IReadOnlyList<DocChunk>>(),
                                          Arg.Any<CancellationToken>()
                                         )
                 )
            .Do(_ => cts.Cancel());

        var indexStage = CreateIndexStage(vectorSearch, auditWriter, broadcaster);

        const string PageAUrl = "https://docs.example.com/page-a";
        var pageAChunks = MakePageChunks(PageAUrl,
                                         RebuildThresholdChunkCount,
                                         depth: 1,
                                         parentUrl: "https://docs.example.com/"
                                        );

        var channel = Channel.CreateUnbounded<DocChunk[]>();
        await channel.Writer.WriteAsync(pageAChunks, TestContext.Current.CancellationToken);

        var job = MakeJob();
        var auditCtx = MakeAuditCtx("test-job-cancel");
        var progress = MakeProgress(job);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                                                                    await indexStage
                                                                        .RunAsync(profile: null,
                                                                            job,
                                                                            auditCtx,
                                                                            channel.Reader,
                                                                            progress,
                                                                            onProgress: null,
                                                                            cts
                                                                        )
                                                               );

        Assert.Single(auditWriter.IndexedCalls);
        var entry = auditWriter.IndexedCalls[index: 0];
        Assert.Equal(PageAUrl, entry.Url);
        Assert.Equal(RebuildThresholdChunkCount, entry.Outcome.ChunkCount);
        Assert.Equal("docs.example.com", entry.Host);
        Assert.Equal(expected: 1, entry.Depth);
        Assert.Equal("https://docs.example.com/", entry.ParentUrl);
        Assert.Equal(auditCtx, entry.Ctx);
    }

    [Fact]
    public async Task EmitsRecordIndexedForEveryPageOnNormalCompletion()
    {
        var auditWriter = new SpyAuditWriter();
        var broadcaster = new SilentMonitorBroadcaster();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        using var cts = new CancellationTokenSource();

        var indexStage = CreateIndexStage(vectorSearch, auditWriter, broadcaster);

        const string PageAUrl = "https://docs.example.com/page-a";
        const string PageBUrl = "https://docs.example.com/page-b";

        var channel = Channel.CreateUnbounded<DocChunk[]>();
        await channel.Writer.WriteAsync(MakePageChunks(PageAUrl,
                                                       count: 5,
                                                       depth: 1,
                                                       parentUrl: "https://docs.example.com/"
                                                      ),
                                        TestContext.Current.CancellationToken
                                       );
        await channel.Writer.WriteAsync(MakePageChunks(PageBUrl,
                                                       count: 3,
                                                       depth: 2,
                                                       parentUrl: PageAUrl
                                                      ),
                                        TestContext.Current.CancellationToken
                                       );
        channel.Writer.Complete();

        var job = MakeJob();
        var auditCtx = MakeAuditCtx("test-job-happy");
        var progress = MakeProgress(job);

        await indexStage.RunAsync(profile: null,
                                  job,
                                  auditCtx,
                                  channel.Reader,
                                  progress,
                                  onProgress: null,
                                  cts
                                 );

        Assert.Equal(expected: 2, auditWriter.IndexedCalls.Count);
        Assert.Contains(auditWriter.IndexedCalls,
                        c => c.Url == PageAUrl && c.Outcome.ChunkCount == 5
                       );
        Assert.Contains(auditWriter.IndexedCalls,
                        c => c.Url == PageBUrl && c.Outcome.ChunkCount == 3
                       );
    }

    [Fact]
    public async Task EmitsNothingWhenNoChunksArrive()
    {
        var auditWriter = new SpyAuditWriter();
        var broadcaster = new SilentMonitorBroadcaster();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        using var cts = new CancellationTokenSource();

        var indexStage = CreateIndexStage(vectorSearch, auditWriter, broadcaster);

        var channel = Channel.CreateUnbounded<DocChunk[]>();
        channel.Writer.Complete();

        var job = MakeJob();
        var auditCtx = MakeAuditCtx("test-job-empty");
        var progress = MakeProgress(job);

        await indexStage.RunAsync(profile: null,
                                  job,
                                  auditCtx,
                                  channel.Reader,
                                  progress,
                                  onProgress: null,
                                  cts
                                 );

        Assert.Empty(auditWriter.IndexedCalls);
    }

    private static IndexStage CreateIndexStage(IVectorSearchProvider vectorSearch,
                                               IScrapeAuditWriter auditWriter,
                                               IMonitorBroadcaster broadcaster) =>
        new IndexStage(vectorSearch,
                       auditWriter,
                       broadcaster,
                       NullLogger<IndexStage>.Instance
                      );

    private static DocChunk[] MakePageChunks(string pageUrl, int count, int depth, string? parentUrl)
    {
        var chunks = new DocChunk[count];
        for(var i = 0; i < count; i++)
        {
            chunks[i] = new DocChunk
                            {
                                Id = $"{pageUrl}#{i}",
                                LibraryId = "test-lib",
                                Version = "1.0",
                                PageUrl = pageUrl,
                                PageTitle = "Test Page",
                                Category = DocCategory.Overview,
                                Content = $"chunk {i}",
                                Depth = depth,
                                ParentUrl = parentUrl
                            };
        }

        return chunks;
    }

    private static ScrapeJob MakeJob() =>
        new ScrapeJob
            {
                RootUrl = "https://docs.example.com/",
                LibraryHint = "test library",
                LibraryId = "test-lib",
                Version = "1.0",
                AllowedUrlPatterns = ["docs.example.com"]
            };

    private static AuditContext MakeAuditCtx(string jobId) =>
        new AuditContext
            {
                JobId = jobId,
                LibraryId = "test-lib",
                Version = "1.0"
            };

    private static ScrapeJobRecord MakeProgress(ScrapeJob job) =>
        new ScrapeJobRecord
            {
                Id = "test-record",
                Job = job
            };

    private sealed record IndexedCall(AuditContext Ctx,
                                      string Url,
                                      string? ParentUrl,
                                      string Host,
                                      int Depth,
                                      AuditPageOutcome Outcome);

    private sealed class SpyAuditWriter : IScrapeAuditWriter
    {
        public List<IndexedCall> IndexedCalls { get; } = new List<IndexedCall>();

        public void RecordSkipped(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth,
                                  AuditSkipReason reason,
                                  string? detail)
        {
        }

        public void RecordFetched(AuditContext ctx,
                                  string url,
                                  string? parentUrl,
                                  string host,
                                  int depth)
        {
        }

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
            => IndexedCalls.Add(new IndexedCall(ctx, url, parentUrl, host, depth, outcome));

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentMonitorBroadcaster : IMonitorBroadcaster
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
}
