// SinglePageContentSelectionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Crawling;

namespace SaddleRAG.Tests.Crawling;

/// <summary>Checks explicit single-page extraction against real browser content and surrounding navigation.</summary>
[Trait("Category", "Integration")]
public sealed class SinglePageContentSelectionTests
{
    [Fact]
    public async Task ExplicitArticleExcludesNavigationAndLargerFrameBeforePersistence()
    {
        var pages = Substitute.For<IPageRepository>();
        PageCrawler crawler = CreateCrawler(pages);

        PageRecord? result = await crawler.FetchSinglePageForProfileAsync(
                                 LibraryId, Version, FixtureUrl(), profile: null, "#release-notes",
                                 TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.StartsWith("Release v2.3.0", result.RawContent, StringComparison.Ordinal);
        Assert.Contains("registration order and shutdown", result.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain("NAVIGATION_MARKER", result.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain("TAG_LIST_MARKER", result.RawContent, StringComparison.Ordinal);
        Assert.DoesNotContain("FRAME_MARKER", result.RawContent, StringComparison.Ordinal);
        Assert.True(result.RawContent.Length < 500);
        await pages.Received(requiredNumberOfCalls: 1)
                   .UpsertPageAsync(result, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("#missing", "No element matched")]
    [InlineData("#empty", "contained no text")]
    [InlineData("[", "could not be evaluated")]
    [InlineData(" ", "must contain a non-whitespace expression")]
    public async Task InvalidExplicitSelectionFailsBeforePersistence(string selector, string expectedReason)
    {
        var pages = Substitute.For<IPageRepository>();
        PageCrawler crawler = CreateCrawler(pages);

        PageContentSelectionException error = await Assert.ThrowsAsync<PageContentSelectionException>(
                                                  () => crawler.FetchSinglePageForProfileAsync(
                                                      LibraryId, Version, FixtureUrl(), profile: null, selector,
                                                      TestContext.Current.CancellationToken));

        Assert.Contains(selector, error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedReason, error.Message, StringComparison.Ordinal);
        await pages.DidNotReceiveWithAnyArgs().UpsertPageAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OmittedSelectorRetainsLegacySinglePageExtraction()
    {
        var pages = Substitute.For<IPageRepository>();
        PageCrawler crawler = CreateCrawler(pages);
        PageRecord? legacy = await crawler.FetchSinglePageAsync(
                                 LibraryId, Version, FixtureUrl(), TestContext.Current.CancellationToken);
        PageRecord? explicitDefault = await crawler.FetchSinglePageForProfileAsync(
                                          LibraryId, Version, FixtureUrl(), profile: null, contentSelector: null,
                                          TestContext.Current.CancellationToken);

        Assert.NotNull(legacy);
        Assert.NotNull(explicitDefault);
        Assert.Equal(legacy.RawContent, explicitDefault.RawContent);
    }

    private static PageCrawler CreateCrawler(IPageRepository pages) =>
        new(pages,
            new GitHubRepoScraper(pages, NullLogger<GitHubRepoScraper>.Instance),
            Substitute.For<IScrapeAuditWriter>(),
            Substitute.For<IMonitorBroadcaster>(),
            NullLogger<PageCrawler>.Instance,
            NullLoggerFactory.Instance);

    private static string FixtureUrl()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "FileScrape", "SinglePage", "release.htm");
        Assert.True(File.Exists(path));
        return new Uri(path).AbsoluteUri;
    }

    private const string LibraryId = "single-page-selection-test";
    private const string Version = "1.0";
}
