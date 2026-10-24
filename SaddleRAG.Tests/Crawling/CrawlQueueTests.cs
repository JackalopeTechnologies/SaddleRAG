// CrawlQueueTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Crawling;
using SaddleRAG.Ingestion.Diagnostics;

namespace SaddleRAG.Tests.Crawling;

[Trait("Category", "Integration")]
public sealed class CrawlQueueTests
{
    [Fact]
    public async Task SharedSidebarAndRepeatedSeedsDoNotQueueAlreadyScheduledTargets()
    {
        string[] paths = Enumerable.Range(0, 12).Select(index => "page" + index + ".html").ToArray();
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(paths);
        var audit = Substitute.For<IScrapeAuditWriter>();
        string[] seeds = Enumerable.Range(0, 20).SelectMany(_ => paths.Select(site.Url)).ToArray();
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, audit, seeds);

        Assert.Equal(13, pages.Count);
        audit.DidNotReceive().RecordSkipped(Arg.Any<AuditContext>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<int>(), AuditSkipReason.AlreadyVisited, Arg.Any<string?>());
    }

    [Fact]
    public async Task SeedAndDiscoveredExtensionAliasesFetchOnceAndPreserveRequestedUrl()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(["target.html"]);
        var audit = Substitute.For<IScrapeAuditWriter>();
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, audit, [site.Url("target")]);

        Assert.Equal(2, pages.Count);
        Assert.Contains(pages, page => page.Url == site.Url("root.html"));
        Assert.Contains(pages, page => page.Url == site.Url("target"));
        Assert.Equal(pages.Count, pages.Select(page => page.Id).Distinct().Count());
    }

    [Fact]
    public async Task ResumedExtensionAliasPreventsSeedAndDiscoveredFetches()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(["target.html"]);
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, Substitute.For<IScrapeAuditWriter>(),
            [site.Url("target")], new HashSet<string> { site.Url("target.html") });

        Assert.Single(pages);
        Assert.Equal(site.Url("root.html"), pages[0].Url);
    }

    [Fact]
    public async Task EntirelyResumedCrawlCompletesWithoutFetchingOrWaitingForWork()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync([]);
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, Substitute.For<IScrapeAuditWriter>(),
            [site.Url("target")], new HashSet<string> { site.Url("root"), site.Url("target.html") });

        Assert.Empty(pages);
    }

    [Fact]
    public async Task ExcludedSeedAliasDoesNotSuppressAllowedCanonicalTarget()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(["blocked"]);
        var audit = Substitute.For<IScrapeAuditWriter>();
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, audit,
            [site.Url("blocked.html"), site.Url("blocked")]);

        Assert.Equal(2, pages.Count);
        Assert.Contains(pages, page => page.Url == site.Url("blocked"));
        audit.Received(1).RecordSkipped(Arg.Any<AuditContext>(), site.Url("blocked.html"), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<int>(), AuditSkipReason.PatternExclude, Arg.Any<string?>());
    }

    [Fact]
    public async Task DepthRejectedAliasCannotSuppressAnEligibleShallowerDiscovery()
    {
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync([], path => path switch
        {
            "/docs/root.html" => ["fast.html", "slow.html"],
            "/docs/fast.html" => ["middle.html"],
            "/docs/middle.html" => ["target.html"],
            "/docs/slow.html" => ["target"],
            _ => []
        }, async context =>
        {
            if (context.Request.Path == "/docs/slow.html")
                await rejected.Task.WaitAsync(TimeSpan.FromSeconds(15), context.RequestAborted);
        });
        var audit = Substitute.For<IScrapeAuditWriter>();
        audit.When(writer => writer.RecordSkipped(Arg.Any<AuditContext>(), site.Url("target.html"),
            Arg.Any<string?>(), Arg.Any<string>(), 3, AuditSkipReason.InScopeDepth, Arg.Any<string?>()))
             .Do(_ => rejected.TrySetResult());
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, audit, [], inScopeDepth: 3);

        Assert.Contains(pages, page => page.Url == site.Url("target") && page.Depth == 2);
        Assert.DoesNotContain(pages, page => page.Url == site.Url("target.html"));
        audit.Received(1).RecordSkipped(Arg.Any<AuditContext>(), site.Url("target.html"), Arg.Any<string?>(),
            Arg.Any<string>(), 3, AuditSkipReason.InScopeDepth, "depth=3 limit=3");
    }

    [Fact]
    public async Task ExplicitForbiddenResponseRetryBypassesCanonicalReservation()
    {
        int attempts = 0;
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(["retry.html"],
            beforeResponse: context =>
            {
                if (context.Request.Path == "/docs/retry.html" && Interlocked.Increment(ref attempts) == 1)
                    context.Response.StatusCode = 403;
                return Task.CompletedTask;
            });
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, Substitute.For<IScrapeAuditWriter>(), []);

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Single(pages, page => page.Url == site.Url("retry.html"));
    }

    [Fact]
    public async Task RenderingEscalationCanRequeueAnAlreadyReservedCanonicalTarget()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync([]);
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, Substitute.For<IScrapeAuditWriter>(), [],
            waitForSelector: "main");

        PageRecord page = Assert.Single(pages);
        Assert.Equal(site.Url("root.html"), page.Url);
    }

    [Fact]
    public async Task RepeatedRejectedSidebarTargetProducesOneAuditEvent()
    {
        string[] paths = Enumerable.Range(0, 6).Select(index => "page" + index + ".html").ToArray();
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(paths.Append("blocked.html"));
        var audit = Substitute.For<IScrapeAuditWriter>();
        IReadOnlyList<PageRecord> pages = await CrawlAsync(site, audit, paths.Select(site.Url).ToArray());

        Assert.Equal(7, pages.Count);
        audit.Received(1).RecordSkipped(Arg.Any<AuditContext>(), site.Url("blocked.html"), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<int>(), AuditSkipReason.PatternExclude, Arg.Any<string?>());
    }

    [Fact]
    public async Task FullAuditBufferFailsCrawlAndOutputInsteadOfReportingCompletion()
    {
        await using CrawlQueueTestSite site = await CrawlQueueTestSite.StartAsync(["target.html"]);
        var repository = Substitute.For<IScrapeAuditRepository>();
        repository.InsertManyAsync(Arg.Any<IEnumerable<ScrapeAuditLogEntry>>(), Arg.Any<CancellationToken>())
                  .ThrowsAsync(new IOException("Unavailable fixture repository"));
        await using var audit = new ScrapeAuditWriter(repository, null, batchSize: 1,
            flushInterval: TimeSpan.FromHours(1), capacity: 1);
        var context = new AuditContext { JobId = "capacity", LibraryId = "queue-test", Version = "1" };
        audit.RecordFetched(context, site.Url("first"), null, "127.0.0.1", 0);
        await Assert.ThrowsAsync<IOException>(() => audit.FlushAsync(TestContext.Current.CancellationToken));
        audit.RecordFetched(context, site.Url("second"), null, "127.0.0.1", 0);
        Channel<PageRecord> output = Channel.CreateUnbounded<PageRecord>();
        IPageCrawler crawler = CreateCrawler(audit);

        InvalidOperationException failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            crawler.CrawlAsync(Job(site), output.Writer,
                       seedUrls: Enumerable.Range(0, 64).Select(index => site.Url("queued" + index)).ToArray(),
                       ct: TestContext.Current.CancellationToken)
                   .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("restore MongoDB", failure.Message, StringComparison.Ordinal);
        int emitted = 0;
        while (output.Reader.TryRead(out _))
            emitted++;
        Assert.InRange(emitted, 1, 8);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => output.Reader.Completion);
    }

    private static IPageCrawler CreateCrawler(IScrapeAuditWriter audit)
    {
        var repository = Substitute.For<IPageRepository>();
        return new PageCrawler(repository,
            new GitHubRepoScraper(repository, NullLogger<GitHubRepoScraper>.Instance), audit,
            Substitute.For<IMonitorBroadcaster>(), NullLogger<PageCrawler>.Instance, NullLoggerFactory.Instance);
    }

    private static ScrapeJob Job(CrawlQueueTestSite site) => new()
    {
        RootUrl = site.Url("root.html"),
        LibraryId = "queue-test",
        Version = "1",
        LibraryHint = "Local crawl queue regression fixture",
        AllowedUrlPatterns = ["^" + System.Text.RegularExpressions.Regex.Escape(site.Origin + "/docs/")],
        ExcludedUrlPatterns = ["blocked\\.html$"],
        MaxPages = 30,
        FetchDelayMs = 0,
        SameHostDepth = 0,
        OffSiteDepth = 0,
        ContentSelector = "main"
    };

    private static async Task<IReadOnlyList<PageRecord>> CrawlAsync(CrawlQueueTestSite site,
        IScrapeAuditWriter audit, IReadOnlyList<string> seeds, IReadOnlySet<string>? resume = null,
        int inScopeDepth = 0, string? waitForSelector = null)
    {
        Channel<PageRecord> output = Channel.CreateUnbounded<PageRecord>();
        await CreateCrawler(audit).CrawlAsync(Job(site) with
            { InScopeDepth = inScopeDepth, WaitForSelector = waitForSelector },
            output.Writer, seedUrls: seeds, resumeUrls: resume,
            ct: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(40),
                TestContext.Current.CancellationToken);
        var pages = new List<PageRecord>();
        await foreach (PageRecord page in output.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            pages.Add(page);
        return pages;
    }
}
