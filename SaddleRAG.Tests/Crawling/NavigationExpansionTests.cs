// NavigationExpansionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

/// <summary>Exercises real browser navigation controls against a local documentation fixture.</summary>
[Trait("Category", "Integration")]
public sealed class NavigationExpansionTests
{
    [Theory]
    [InlineData(IngestionPersistenceMode.Full)]
    [InlineData(IngestionPersistenceMode.DryRun)]
    public async Task CrawlPreservesArticleAndFindsLinksBehindExpansionOnlyControls(IngestionPersistenceMode mode)
    {
        string rootUrl = FixtureUrl("index.htm");
        IReadOnlyList<PageRecord> pages = await CrawlAsync(rootUrl, mode, TestContext.Current.CancellationToken);

        PageRecord root = Assert.Single(pages, page => new Uri(page.Url) == new Uri(rootUrl));
        Assert.Equal("Original reference", root.Title);
        Assert.Contains(OriginalMarker, root.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongMarker, root.RawContent, StringComparison.Ordinal);
        Assert.Contains(pages, page => new Uri(page.Url) == new Uri(FixtureUrl("existing.htm")));
        Assert.Contains(pages, page => new Uri(page.Url) == new Uri(FixtureUrl("revealed.htm")));
        Assert.DoesNotContain(pages, page => page.Url.EndsWith("wrong.htm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedNavigationCannotReplaceCapturedArticleOrItsOriginalLinks()
    {
        string rootUrl = FixtureUrl("unexpected.htm");
        IReadOnlyList<PageRecord> pages = await CrawlAsync(rootUrl,
                                                           IngestionPersistenceMode.Full,
                                                           TestContext.Current.CancellationToken);

        PageRecord root = Assert.Single(pages, page => new Uri(page.Url) == new Uri(rootUrl));
        Assert.Equal("Original reference", root.Title);
        Assert.Contains(OriginalMarker, root.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongMarker, root.RawContent, StringComparison.Ordinal);
        Assert.Contains(pages, page => new Uri(page.Url) == new Uri(FixtureUrl("existing.htm")));
        Assert.DoesNotContain(pages, page => new Uri(page.Url) == new Uri(FixtureUrl("revealed.htm")));
    }

    [Fact]
    public async Task SinglePageFetchDoesNotActivateNavigationControls()
    {
        PageCrawler crawler = CreateCrawler();
        string rootUrl = FixtureUrl("index.htm");
        PageRecord? page = await crawler.FetchSinglePageAsync(LibraryId,
                                                               Version,
                                                               rootUrl,
                                                               TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Original reference", page.Title);
        Assert.Contains(OriginalMarker, page.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongMarker, page.RawContent, StringComparison.Ordinal);
    }

    private static PageCrawler CreateCrawler(ILogger<PageCrawler>? logger = null)
    {
        var pages = Substitute.For<IPageRepository>();
        var result = new PageCrawler(pages,
                                     new GitHubRepoScraper(pages, NullLogger<GitHubRepoScraper>.Instance),
                                     Substitute.For<IScrapeAuditWriter>(),
                                     Substitute.For<IMonitorBroadcaster>(),
                                     logger ?? NullLogger<PageCrawler>.Instance,
                                     NullLoggerFactory.Instance);
        return result;
    }

    private static async Task<IReadOnlyList<PageRecord>> CrawlAsync(string rootUrl,
                                                                    IngestionPersistenceMode mode,
                                                                    CancellationToken ct)
    {
        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = LibraryId,
                          Version = Version,
                          LibraryHint = "Local navigation expansion regression fixture",
                          AllowedUrlPatterns = [""],
                          ExcludedUrlPatterns = ["wrong\\.htm$"],
                          MaxPages = MaxPages,
                          FetchDelayMs = 0,
                          OffSiteDepth = 0,
                          SameHostDepth = 0
                      };
        Channel<PageRecord> channel = Channel.CreateUnbounded<PageRecord>();
        var logger = Substitute.For<ILogger<PageCrawler>>();
        await ((IPageCrawler)CreateCrawler(logger)).CrawlAsync(job, channel.Writer, persistMode: mode, ct: ct);
        var result = new List<PageRecord>();
        await foreach(PageRecord page in channel.Reader.ReadAllAsync(ct))
            result.Add(page);
        Assert.True(result.Count > 0,
                    string.Join(Environment.NewLine,
                                logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log")
                                      .Select(call => $"{call.GetArguments()[2]} {call.GetArguments()[3]}")));
        return result;
    }

    private static string FixtureUrl(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "FileScrape", "Navigation", fileName);
        Assert.True(File.Exists(path), $"Navigation fixture must be copied to output: {path}");
        return new Uri(path).AbsoluteUri;
    }

    private const string LibraryId = "navigation-expansion-test";
    private const string Version = "1.0";
    private const string OriginalMarker = "ORIGINAL_ARTICLE_MARKER";
    private const string WrongMarker = "WRONG_ARTICLE_MARKER";
    private const int MaxPages = 4;
}
