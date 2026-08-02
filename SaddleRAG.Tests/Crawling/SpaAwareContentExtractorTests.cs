// SpaAwareContentExtractorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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

    // www.advancedinstaller.com is a static documentation site that mounts Vue for one
    // small widget — the page-rating box. Vue 3 stamps data-v-app on whatever it mounts,
    // so [data-v-app] matched DIV#rating-component (177 chars, "Did you find this page
    // useful?") and short-circuited ahead of main (14,064 chars of documentation prose).
    // Every indexed page held the footer instead of the article.
    [Fact]
    public async Task FrameworkRootEnclosedByStandardContainerLosesToStandardSelector()
    {
        const string WidgetText = "Did you find this page useful?";
        const string ArticleText = "In this tab you can configure the digital signature of your package.";

        var widgetElement = MakeElement(WidgetText);
        var mainElement = MakeElement(ArticleText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(widgetElement);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<bool>(Arg.Any<string>(), Arg.Any<object?>()).Returns(true);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(ArticleText, result);
    }

    // The mirror case: on a genuine SPA the framework root contains the content region
    // rather than being contained by it, so the fast path must still win.
    [Fact]
    public async Task FrameworkRootThatEnclosesNothingKeepsFastPath()
    {
        const string AppText = "single-page app shell content";

        var appElement = MakeElement(AppText);
        var mainElement = MakeElement("nested main content");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(appElement);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<bool>(Arg.Any<string>(), Arg.Any<object?>()).Returns(false);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(AppText, result);
    }

    // The enclosure probe is a refinement, not a gate: if it cannot run, the framework
    // hit stands rather than the page silently losing its fast path.
    [Fact]
    public async Task EnclosureProbeFailureLeavesFrameworkHitStanding()
    {
        const string AppText = "app shell content";

        var appElement = MakeElement(AppText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(appElement);
        page.EvaluateAsync<bool>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns<bool>(_ => throw new PlaywrightException("simulated"));

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(AppText, result);
    }

    // A user-supplied selector is an assertion about where the content is; the enclosure
    // probe must not second-guess it even when it points inside a standard container.
    [Fact]
    public async Task UserSelectorSurvivesEnclosureProbe()
    {
        const string UserSelector = "#rating-component";
        const string UserText = "deliberately scoped widget text";

        var userElement = MakeElement(UserText);
        var mainElement = MakeElement("much longer main content");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync(UserSelector).Returns(userElement);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<bool>(Arg.Any<string>(), Arg.Any<object?>()).Returns(true);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, UserSelector, CancellationToken.None);

        Assert.Equal(UserText, result);
    }

    private static IElementHandle MakeElement(string innerText)
    {
        var element = Substitute.For<IElementHandle>();
        element.InnerTextAsync().Returns(innerText);
        return element;
    }
}
