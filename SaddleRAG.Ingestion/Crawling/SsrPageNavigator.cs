// SsrPageNavigator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Navigator strategy for server-side rendered documentation sites.
///     Waits only for <see cref="WaitUntilState.DOMContentLoaded" /> —
///     no extra settle. Content is already in the HTML at first byte,
///     so any additional wait is wasted time.
///     <para>
///         Used as the default navigator at crawl start. The
///         <c>EscalationController</c> swaps to <see cref="SpaPageNavigator" />
///         if shell sniffing detects an SPA framework in the first three
///         pages.
///     </para>
/// </summary>
public sealed class SsrPageNavigator : IPageNavigator
{
    public SsrPageNavigator(ILogger<SsrPageNavigator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        mLogger = logger;
    }

    private readonly ILogger<SsrPageNavigator> mLogger;

    public async Task<NavigationResult> NavigateAsync(IPage page, string url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(url);

        ct.ThrowIfCancellationRequested();

        var response = await page.GotoAsync(url,
                                            new PageGotoOptions
                                                {
                                                    WaitUntil = WaitUntilState.DOMContentLoaded,
                                                    Timeout = PageTimeoutMs
                                                }
                                           );

        int domCount = -1;
        if (response is { Ok: true })
            domCount = await PageMetrics.MeasureContentNodesAsync(page, mLogger);

        string responseText = await SafeReadResponseTextAsync(response, url);
        return new NavigationResult
                   {
                       Response = response,
                       ResponseText = responseText,
                       DomCount = domCount
                   };
    }

    private async Task<string> SafeReadResponseTextAsync(IResponse? response, string url)
    {
        string result = string.Empty;
        if (response != null)
        {
            try
            {
                result = await response.TextAsync();
            }
            catch(PlaywrightException ex)
            {
                mLogger.LogWarning(ex,
                                   "Failed to read response body for {Url}; SPA shell sniffing for this page will return no signal",
                                   url
                                  );
            }
            catch(InvalidOperationException ex)
            {
                mLogger.LogWarning(ex,
                                   "Response body unavailable for {Url}; SPA shell sniffing for this page will return no signal",
                                   url
                                  );
            }
        }

        return result;
    }

    private const int PageTimeoutMs = 30000;
}
