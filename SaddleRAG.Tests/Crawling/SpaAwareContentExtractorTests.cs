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
    public async Task BaselineHeuristicUsedWhenSelectorsAllMiss()
    {
        const string HeuristicText = "heuristic-extracted body content";

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(HeuristicText);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(HeuristicText, result);
    }

    [Fact]
    public async Task ReturnsEmptyWhenAllStrategiesFail()
    {
        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(string.Empty);

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

    // installeranalytics.com (Caphyon) is a static docs site with NO <main>/<article>. It mounts
    // Vue 3 for one small widget — the page-rating box (DIV#rating-component) — and Vue stamps
    // data-v-app on its mount element. So [data-v-app] matched the ~170-char rating widget and
    // short-circuited ahead of the ~3,092-char article. With no standard container on the page,
    // the old enclosure probe could not demote it. The dominance gate must: reject the widget
    // (signposted by its id token "rating" AND far below the baseline), find no standard match,
    // and fall to the baseline — the article.
    [Fact]
    public async Task WidgetLosesToArticleWhenPageHasNoLandmark()
    {
        string articleText = new('a', 3092);
        const string WidgetText = "Did you find this page useful?";

        var widgetElement = MakeElement(WidgetText, id: "rating-component", className: "margin-b-30px");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(widgetElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(articleText);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(articleText, result);
    }

    // The advancedinstaller.com sibling case: same rating widget, but that page DOES have a <main>.
    // The widget is signposted by its id, so it loses; <main> holds the dominant content and wins.
    [Fact]
    public async Task SignpostedFrameworkWidgetLosesToDominantMain()
    {
        string articleText = new('m', 14064);
        const string WidgetText = "Did you find this page useful?";

        var widgetElement = MakeElement(WidgetText, id: "rating-component");
        var mainElement = MakeElement(articleText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(widgetElement);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(articleText);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(articleText, result);
    }

    // A framework root that is NOT signposted but is far smaller than the baseline is still a
    // widget, not the app root — it must lose to the baseline on dominance alone.
    [Fact]
    public async Task SubDominantFrameworkMatchWithoutSignpostFallsToBaseline()
    {
        string baseline = new('b', 3000);
        // ~5% of baseline, well under the 0.15 lenient ratio
        string smallApp = new('x', 150);

        var appElement = MakeElement(smallApp, id: "app");

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("#app").Returns(appElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(baseline);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(baseline, result);
    }

    // A genuine SPA: the framework root holds essentially all the page's content, so it is
    // dominant (≈ baseline) and the fast path is kept.
    [Fact]
    public async Task GenuineSpaFrameworkRootIsKeptWhenDominant()
    {
        string appText = new('s', 5000);

        var appElement = MakeElement(appText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(appElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(appText);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(appText, result);
    }

    // A semantic landmark is trusted at the lenient ratio: even at ~20% of the baseline it is
    // kept, because <main>/<article>/[role=main] are strong signals.
    [Fact]
    public async Task SemanticLandmarkKeptAtLenientDominance()
    {
        string baseline = new('b', 1000);
        // 20% >= 0.15 lenient ratio
        string mainText = new('m', 200);

        var mainElement = MakeElement(mainText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(baseline);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(mainText, result);
    }

    // A generic content-class selector is weak and easily hits a widget subtree, so it is held to
    // the strict ratio: at 30% of the baseline it is rejected and the baseline wins.
    [Fact]
    public async Task GenericSelectorRejectedBelowStrictDominance()
    {
        string baseline = new('b', 1000);
        // 30% < 0.5 strict ratio
        string contentText = new('c', 300);

        var contentElement = MakeElement(contentText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync(".content").Returns(contentElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(baseline);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(baseline, result);
    }

    // The mirror: a generic content-class selector that IS dominant (>= 0.5) is kept.
    [Fact]
    public async Task GenericSelectorKeptAtOrAboveStrictDominance()
    {
        string baseline = new('b', 1000);
        // 60% >= 0.5 strict ratio
        string contentText = new('c', 600);

        var contentElement = MakeElement(contentText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync(".content").Returns(contentElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(baseline);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(contentText, result);
    }

    // When the baseline heuristic returns nothing (a genuinely tiny page under the heuristic's
    // MIN_TEXT floor), a non-signposted selector match is still accepted, so short pages keep
    // extracting rather than silently going empty.
    [Fact]
    public async Task NonSignpostedMatchAcceptedWhenBaselineEmpty()
    {
        const string ShortMain = "a short but real documentation page";

        var mainElement = MakeElement(ShortMain);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(string.Empty);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(ShortMain, result);
    }

    // The baseline computation failing must not lose the page its fast path: a framework hit
    // stands rather than the page silently losing extraction.
    [Fact]
    public async Task BaselineFailureLeavesFrameworkHitStanding()
    {
        const string AppText = "app shell content";

        var appElement = MakeElement(AppText);

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(appElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns<string>(_ => throw new PlaywrightException("simulated"));

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(AppText, result);
    }

    // A user-supplied selector is an assertion about where the content is; it is returned
    // verbatim, ungated by the signpost or dominance checks, even when it points at a small
    // signposted widget.
    [Fact]
    public async Task UserSelectorIsUngated()
    {
        const string UserSelector = "#rating-component";
        const string UserText = "deliberately scoped widget text";

        var userElement = MakeElement(UserText, id: "rating-component");
        var mainElement = MakeElement(new string('m', 5000));

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync(UserSelector).Returns(userElement);
        page.QuerySelectorAsync("main").Returns(mainElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(new string('m', 5000));

        string result = await SpaAwareContentExtractor.ExtractAsync(page, UserSelector, CancellationToken.None);

        Assert.Equal(UserText, result);
    }

    // A small rating form with no boilerplate id/class token is still caught structurally: a
    // radio/checkbox vote control plus little text is a widget, not main content.
    [Fact]
    public async Task StructurallyDetectedVoteWidgetLosesToBaseline()
    {
        string baseline = new('b', 3000);
        const string WidgetText = "Rate this";

        var widgetElement = MakeElement(WidgetText);
        widgetElement.QuerySelectorAsync("input[type=radio], input[type=checkbox]").Returns(Substitute.For<IElementHandle>());

        var page = Substitute.For<IPage>();
        page.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        page.QuerySelectorAsync("[data-v-app]").Returns(widgetElement);
        page.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns(baseline);

        string result = await SpaAwareContentExtractor.ExtractAsync(page, waitForSelector: null, CancellationToken.None);

        Assert.Equal(baseline, result);
    }

    private static IElementHandle MakeElement(string innerText, string? id = null, string? className = null)
    {
        var element = Substitute.For<IElementHandle>();
        element.InnerTextAsync().Returns(innerText);
        element.GetAttributeAsync("id").Returns(id);
        element.GetAttributeAsync("class").Returns(className);
        // A real element has no descendants unless a test adds them; without this, NSubstitute
        // auto-substitutes a non-null IElementHandle and the vote-form check misfires.
        element.QuerySelectorAsync(Arg.Any<string>()).Returns((IElementHandle?) null);
        return element;
    }
}
