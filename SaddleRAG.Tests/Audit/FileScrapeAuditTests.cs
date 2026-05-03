// FileScrapeAuditTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that <see cref="PageCrawler.DryRunAsync"/> correctly crawls a local
///     file:// documentation tree, records consistent audit host strings, and
///     skips off-site links without attempting to fetch them.
/// </summary>
public sealed class FileScrapeAuditTests
{
    #region DryRunOverFileUrlRecordsFetchEventsForReachablePages test

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DryRunOverFileUrlRecordsFetchEventsForReachablePages()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "FileScrape");
        Assert.True(Directory.Exists(fixtureRoot),
                    $"Test fixture directory must be copied to output. Looked for: {fixtureRoot}");

        var rootUrl = new Uri(Path.Combine(fixtureRoot, "index.htm")).AbsoluteUri;

        var auditWriter = new SpyAuditWriter();
        var pageRepo = new NullPageRepository();
        var gitHubScraper = new GitHubRepoScraper(pageRepo, NullLogger<GitHubRepoScraper>.Instance);
        var crawler = new PageCrawler(pageRepo, gitHubScraper, auditWriter, NullLogger<PageCrawler>.Instance);

        // AllowedUrlPatterns = [""] — empty-string regex matches every URL, which is the
        // same default the MCP tool applies when the root host is empty (file:// scheme).
        // ExcludedUrlPatterns = [] and OffSiteDepth = 0 together ensure the external
        // https://example.com/external link is skipped without a real network fetch.
        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = "file-scrape-test",
                          Version = "1.0",
                          LibraryHint = "file:// scrape integration test",
                          AllowedUrlPatterns = [""],
                          ExcludedUrlPatterns = [],
                          MaxPages = 0,
                          FetchDelayMs = 0,
                          SameHostDepth = 5,
                          OffSiteDepth = 0
                      };

        await crawler.DryRunAsync(job, "file-scrape-test", "1.0", "test-job-file",
                                  ct: TestContext.Current.CancellationToken);

        // All 4 local .htm files must be fetched.
        Assert.Equal(4, auditWriter.FetchedCalls.Count);

        // The off-site https link must be skipped (depth-exceeded or pattern-based).
        Assert.Contains(auditWriter.SkippedCalls,
                        c => c.Reason == AuditSkipReason.OffSiteDepth
                          || c.Reason == AuditSkipReason.PatternMissAllowed);

        // All fetched audit events must carry the same host string — the fix for the
        // dryrun vs. live path inconsistency.  For file:// URLs the canonical host is
        // "" (empty string), matching what SafeGetHost and the live-crawl path produce.
        var distinctHosts = auditWriter.FetchedCalls.Select(c => c.Host).Distinct().ToList();
        Assert.Single(distinctHosts);
    }

    #endregion

    #region Spy types

    private sealed record AuditCall(AuditContext Context, string Url, string Host, AuditSkipReason? Reason);

    private sealed class SpyAuditWriter : IScrapeAuditWriter
    {
        public List<AuditCall> FetchedCalls { get; } = new();
        public List<AuditCall> SkippedCalls { get; } = new();

        public void RecordSkipped(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                                  AuditSkipReason reason, string? detail)
            => SkippedCalls.Add(new AuditCall(ctx, url, host, reason));

        public void RecordFetched(AuditContext ctx, string url, string? parentUrl, string host, int depth)
            => FetchedCalls.Add(new AuditCall(ctx, url, host, Reason: null));

        public void RecordFailed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
                                 string error)
        {
        }

        public void RecordIndexed(AuditContext ctx, string url, string? parentUrl, string host, int depth,
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

        public Task<IReadOnlyList<PageRecord>> GetPagesAsync(string libraryId, string version,
                                                              CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PageRecord>>(Array.Empty<PageRecord>());

        public Task<PageRecord?> GetPageByUrlAsync(string libraryId, string version, string url,
                                                    CancellationToken ct = default)
            => Task.FromResult<PageRecord?>(null);

        public Task<int> GetPageCountAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<long> DeleteAsync(string libraryId, string version, CancellationToken ct = default)
            => Task.FromResult(0L);
    }

    #endregion
}
