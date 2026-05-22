// SpaPageNavigatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class SpaPageNavigatorTests
{
    [Fact]
    public async Task AppliesSpaWaitMsAfterNetworkIdle()
    {
        var response = Substitute.For<IResponse>();
        response.Ok.Returns(returnThis: true);
        response.TextAsync().Returns("<html></html>");

        var page = Substitute.For<IPage>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns(Task.FromResult<IResponse?>(response));

        var navigator = new SpaPageNavigator(waitForSelector: null,
                                             spaWaitMs: 500,
                                             NullLogger<SpaPageNavigator>.Instance
                                            );

        var navResult = await navigator.NavigateAsync(page, "http://example.com", CancellationToken.None);

        Assert.NotNull(navResult.Response);
        await page.Received().WaitForTimeoutAsync(SpaSettleDelayMs);
        await page.Received().WaitForTimeoutAsync(SpaExtraWaitMs);
    }

    [Fact]
    public async Task NoSpaWaitMsOnlyAppliesFixedSettle()
    {
        var response = Substitute.For<IResponse>();
        response.Ok.Returns(returnThis: true);
        response.TextAsync().Returns("<html></html>");

        var page = Substitute.For<IPage>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns(Task.FromResult<IResponse?>(response));

        var navigator = new SpaPageNavigator(waitForSelector: null,
                                             spaWaitMs: 0,
                                             NullLogger<SpaPageNavigator>.Instance
                                            );

        await navigator.NavigateAsync(page, "http://example.com", CancellationToken.None);

        await page.Received(requiredNumberOfCalls: 1).WaitForTimeoutAsync(SpaSettleDelayMs);
    }

    [Fact]
    public async Task WaitForSelectorWhenSuppliedIsCalled()
    {
        var response = Substitute.For<IResponse>();
        response.Ok.Returns(returnThis: true);
        response.TextAsync().Returns("<html></html>");

        var page = Substitute.For<IPage>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns(Task.FromResult<IResponse?>(response));

        var navigator = new SpaPageNavigator(waitForSelector: ".mud-main-content",
                                             spaWaitMs: 0,
                                             NullLogger<SpaPageNavigator>.Instance
                                            );

        await navigator.NavigateAsync(page, "http://example.com", CancellationToken.None);

        await page.Received().WaitForSelectorAsync(".mud-main-content", Arg.Any<PageWaitForSelectorOptions>());
    }

    private const int SpaSettleDelayMs = 300;
    private const int SpaExtraWaitMs = 500;
}
