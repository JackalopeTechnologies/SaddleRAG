// CrawlOutcomeEvaluatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies the gate that stops a broken crawl from reporting success. The
///     advanced-installer scrape indexed one page, errored on roughly 1,485, and still
///     reported Completed — so search answered from a single page of footer boilerplate
///     while looking healthy. The gate must catch that without punishing a small but
///     legitimate documentation set.
/// </summary>
public sealed class CrawlOutcomeEvaluatorTests
{
    // The regression this type exists for.
    [Fact]
    public void OnePageAgainstFifteenHundredErrorsIsAFailedCrawl() =>
        Assert.True(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted: 1, errorCount: 1485));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 50)]
    [InlineData(-1, 0)]
    public void IndexingNothingIsAlwaysAFailedCrawl(int pagesCompleted, int errorCount) =>
        Assert.True(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted, errorCount));

    // Errors must clear both the floor and the ratio.
    [Fact]
    public void ErrorsJustOverTheRatioAreAFailedCrawl() =>
        Assert.True(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted: 5, errorCount: 60));

    [Fact]
    public void ErrorsExactlyAtTheRatioAreNotAFailedCrawl() =>
        Assert.False(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted: 5, errorCount: 50));

    // A healthy large crawl always carries some dead links.
    [Fact]
    public void HealthyCrawlWithScatteredErrorsCompletes() =>
        Assert.False(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted: 1485, errorCount: 20));

    // A genuinely small documentation set is not a broken crawl — this is the
    // false positive the ratio test exists to avoid.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(2, 9)]
    public void SmallButCleanCrawlCompletes(int pagesCompleted, int errorCount) =>
        Assert.False(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted, errorCount));

    // Below the significance floor the ratio is not consulted: one page with a
    // couple of broken links is still a real, if thin, harvest.
    [Fact]
    public void ErrorsBelowTheSignificanceFloorCompletes() =>
        Assert.False(CrawlOutcomeEvaluator.IndicatesFailedCrawl(pagesCompleted: 1, errorCount: 9));
}
