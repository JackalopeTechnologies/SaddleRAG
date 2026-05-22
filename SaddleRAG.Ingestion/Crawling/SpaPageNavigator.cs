// SpaPageNavigator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Navigator strategy for single-page application (SPA) documentation
///     sites — Blazor WASM, React CSR, Vue CSR, Angular, SvelteKit CSR,
///     Flutter Web, Next.js CSR, Nuxt CSR. Content is rendered by the
///     framework's JavaScript after the initial HTML response, so
///     extracting at <see cref="WaitUntilState.DOMContentLoaded" /> (the
///     SSR default) returns an empty shell. This navigator waits for
///     hydration to complete.
///     <para>
///         Five-stage wait:
///         (1) GotoAsync(DOMContentLoaded) captures the IResponse,
///         (2) WaitForLoadStateAsync(NetworkIdle) with a hard cap,
///         (3) fixed <see cref="SpaSettleDelayMs" /> settle,
///         (4) optional user-supplied <c>spaWaitMs</c> extra delay,
///         (5) optional user-supplied <c>waitForSelector</c> with a
///         <see cref="WaitForSelectorTimeoutMs" /> cap (warn-and-continue
///         on timeout — the content extractor's biggest-text-container
///         heuristic catches the fallback case).
///     </para>
/// </summary>
public sealed class SpaPageNavigator : IPageNavigator
{
    public SpaPageNavigator(string? waitForSelector, int spaWaitMs, ILogger<SpaPageNavigator> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spaWaitMs);
        ArgumentNullException.ThrowIfNull(logger);

        mWaitForSelector = waitForSelector;
        mSpaWaitMs = spaWaitMs;
        mLogger = logger;
    }

    private readonly string? mWaitForSelector;
    private readonly int mSpaWaitMs;
    private readonly ILogger<SpaPageNavigator> mLogger;

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

        if (response is { Ok: true })
        {
            await WaitForNetworkIdleWithCapAsync(page, url, ct);
            await SafeTimeoutAsync(page, SpaSettleDelayMs, ct);

            if (mSpaWaitMs > 0)
                await SafeTimeoutAsync(page, mSpaWaitMs, ct);

            if (!string.IsNullOrEmpty(mWaitForSelector))
                await WaitForSelectorWithWarnAsync(page, mWaitForSelector, url, ct);
        }

        string responseText = await SafeReadResponseTextAsync(response, url);
        return new NavigationResult { Response = response, ResponseText = responseText };
    }

    private async Task WaitForNetworkIdleWithCapAsync(IPage page, string url, CancellationToken ct)
    {
        try
        {
            var waitTask = page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var timeoutTask = Task.Delay(NetworkIdleTimeoutMs, ct);
            var completed = await Task.WhenAny(waitTask, timeoutTask);

            if (completed == waitTask)
                await waitTask;
            else
            {
                ct.ThrowIfCancellationRequested();
                mLogger.LogDebug("NetworkIdle wait capped at {Timeout}ms for {Url}",
                                 NetworkIdleTimeoutMs,
                                 url
                                );
            }
        }
        catch(PlaywrightException ex)
        {
            mLogger.LogDebug(ex, "NetworkIdle wait failed for {Url}", url);
        }
    }

    private async Task SafeTimeoutAsync(IPage page, int ms, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForTimeoutAsync(ms);
        }
        catch(PlaywrightException ex)
        {
            mLogger.LogDebug(ex, "WaitForTimeout({Ms}) failed", ms);
        }
    }

    private async Task WaitForSelectorWithWarnAsync(IPage page,
                                                    string selector,
                                                    string url,
                                                    CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await page.WaitForSelectorAsync(selector,
                                            new PageWaitForSelectorOptions
                                                {
                                                    Timeout = WaitForSelectorTimeoutMs
                                                }
                                           );
        }
        catch(TimeoutException)
        {
            mLogger.LogWarning("WaitForSelector '{Selector}' timed out on {Url}; continuing with available content",
                               selector,
                               url
                              );
        }
        catch(PlaywrightException ex)
        {
            mLogger.LogWarning(ex,
                               "WaitForSelector '{Selector}' failed on {Url}; continuing with available content",
                               selector,
                               url
                              );
        }
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
                mLogger.LogDebug(ex, "Failed to read response body for {Url}", url);
            }
            catch(InvalidOperationException ex)
            {
                mLogger.LogDebug(ex, "Response body unavailable for {Url}", url);
            }
        }

        return result;
    }

    private const int PageTimeoutMs = 30000;
    private const int NetworkIdleTimeoutMs = 8000;
    private const int SpaSettleDelayMs = 300;
    private const int WaitForSelectorTimeoutMs = 10000;
}
