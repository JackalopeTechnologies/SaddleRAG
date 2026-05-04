// DryRunAuditTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that <see cref="PageCrawler.DryRunAsync" /> records skip and fetch
///     audit events for a minimal single-page dry run.
/// </summary>
public sealed class DryRunAuditTests
{
    private sealed class SpyAuditWriter : IScrapeAuditWriter
    {
        public List<AuditContext> FetchedCalls { get; } = new List<AuditContext>();
        public List<AuditContext> SkippedCalls { get; } = new List<AuditContext>();

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

        public void RecordError(string jobId, string message)
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
            => Task.FromResult<IReadOnlyList<PageRecord>>(Array.Empty<PageRecord>());

        public Task<PageRecord?> GetPageByUrlAsync(string libraryId,
                                                   string version,
                                                   string url,
                                                   CancellationToken ct = default)
            => Task.FromResult<PageRecord?>(result: null);

        public Task<int> GetPageCountAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(result: 0);

        public Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(result: 0L);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DryRunRecordsAuditEntriesForLibraryAndVersion()
    {
        var auditWriter = new SpyAuditWriter();
        var pageRepo = new NullPageRepository();
        var gitHubScraper = new GitHubRepoScraper(pageRepo, NullLogger<GitHubRepoScraper>.Instance);
        var crawler = new PageCrawler(pageRepo,
                                      gitHubScraper,
                                      auditWriter,
                                      new NullMonitorBroadcaster(),
                                      NullLogger<PageCrawler>.Instance
                                     );

        var job = new ScrapeJob
                      {
                          RootUrl = "https://example.com/",
                          LibraryId = "dryrun-test",
                          Version = "1.0",
                          LibraryHint = "Dry run test",
                          AllowedUrlPatterns = ["example.com"],
                          ExcludedUrlPatterns = [],
                          MaxPages = 1,
                          FetchDelayMs = 0,
                          SameHostDepth = 1,
                          OffSiteDepth = 0
                      };

        const string LibraryId = "dryrun-test";
        const string Version = "1.0";
        const string JobId = "test-job-01";

        await crawler.DryRunAsync(job,
                                  LibraryId,
                                  Version,
                                  JobId,
                                  ct: TestContext.Current.CancellationToken
                                 );

        Assert.True(auditWriter.FetchedCalls.Count > 0 || auditWriter.SkippedCalls.Count > 0,
                    "DryRunAsync should have recorded at least one audit event."
                   );

        bool allMatchLibrary = auditWriter.FetchedCalls.Concat(auditWriter.SkippedCalls)
                                          .All(c => c.LibraryId == LibraryId && c.Version == Version && c.JobId == JobId
                                              );
        Assert.True(allMatchLibrary, "All audit calls should carry the supplied library, version, and jobId.");
    }
}
