// PageMetrics.cs
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
///     Playwright-side measurement helpers shared by the page navigators
///     and the crawler. The substantial-content-node count is the input
///     to <c>RenderModeVoter</c> — the delta between the DOM-ready count
///     and the post-Load count classifies a site as SSR or SPA.
/// </summary>
internal static class PageMetrics
{
    /// <summary>
    ///     Count substantial-content nodes (block-level elements whose
    ///     inner text contains more than <see cref="ContentNodeMinWords" />
    ///     words). Returns -1 when Playwright fails to evaluate — callers
    ///     treat negative counts as "indeterminate" and skip downstream
    ///     sampling.
    /// </summary>
    public static async Task<int> MeasureContentNodesAsync(IPage page, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(logger);

        int res = -1;

        try
        {
            res = await page.MainFrame.EvaluateAsync<int>(smContentNodeScript);
        }
        catch(PlaywrightException ex)
        {
            logger.LogDebug(ex, "Content node measurement failed on {Url}", page.Url);
        }

        return res;
    }

    private const int ContentNodeMinWords = 7;

    private static readonly string smContentNodeScript =
        "() => [...document.querySelectorAll('p,li,pre,code,h1,h2,h3,h4,blockquote,td')]" +
        $".filter(el => {{ const t = el.innerText; return t && t.trim().split(/\\s+/).length > {ContentNodeMinWords}; }}).length";
}
