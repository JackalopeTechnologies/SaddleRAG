// SpaAwareContentExtractorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Playwright;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class SpaAwareContentExtractorTests
{
    [Fact]
    public async Task UserSuppliedSelectorWinsOverFrameworkFastPath()
    {
        const string UserSelector = "#user-supplied";
        const string UserText = "user supplied content";

        var userElement = MakeElement(UserText);
        var mudElement = MakeElement("mud content");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(UserSelector).Returns(userElement);
        page.QuerySelectorAsync(".mud-main-content").Returns(mudElement);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, UserSelector, CancellationToken.None);

        Assert.Equal(UserText, result);
        await page.Received().QuerySelectorAsync(UserSelector);
    }

    [Fact]
    public async Task MudBlazorSelectorTriedAheadOfStandardSelectors()
    {
        const string MudText = "mud content text";

        var mudElement = MakeElement(MudText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(".mud-main-content").Returns(mudElement);
        page.QuerySelectorAsync("main").Returns((IElementHandle?) null);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(MudText, result);
    }

    [Fact]
    public async Task StandardSelectorUsedWhenNoFrameworkMatch()
    {
        const string MainText = "main element content";

        var mainElement = MakeElement(MainText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("main").Returns(mainElement);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(MainText, result);
    }

    [Fact]
    public async Task BiggestContainerHeuristicUsedWhenSelectorsAllMiss()
    {
        const string HeuristicText = "heuristic-extracted body content";

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.EvaluateAsync<string>(Arg.Any<string>()).Returns(HeuristicText);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(HeuristicText, result);
    }

    [Fact]
    public async Task ReturnsEmptyWhenAllStrategiesFail()
    {
        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.EvaluateAsync<string>(Arg.Any<string>()).Returns(string.Empty);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task UserSelectorMissesFallsThroughToFrameworkSelectors()
    {
        const string UserSelector = "#missing";
        const string MudText = "mud-main-content fallback";

        var mudElement = MakeElement(MudText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(UserSelector).Returns((IElementHandle?) null);
        page.QuerySelectorAsync(".mud-main-content").Returns(mudElement);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, UserSelector, CancellationToken.None);

        Assert.Equal(MudText, result);
    }

    [Fact]
    public async Task EmptyTextFromSelectorFallsThroughToNext()
    {
        var emptyElement = MakeElement(string.Empty);
        var mudElement = MakeElement("real content");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(".mud-main-content").Returns(emptyElement);
        page.QuerySelectorAsync(".mud-container").Returns(mudElement);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal("real content", result);
    }

    [Fact]
    public async Task PlaywrightExceptionOnSelectorSwallowedGoesToNext()
    {
        var nextElement = MakeElement("next-strategy content");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(".mud-main-content").Returns<IElementHandle?>(_ => throw new PlaywrightException("simulated"));
        page.QuerySelectorAsync(".mud-container").Returns(nextElement);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal("next-strategy content", result);
    }

    private static IElementHandle MakeElement(string innerText)
    {
        var element = Substitute.For<IElementHandle>();
        element.InnerTextAsync().Returns(innerText);
        return element;
    }
}
