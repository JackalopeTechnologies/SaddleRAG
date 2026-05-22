// SpaAwareContentExtractor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Content extractor with SPA-aware selector priority. Strategy:
///     <list type="number">
///         <item>
///             User-supplied <c>waitForSelector</c> (when non-null) — the
///             power-user override. They asserted where the content is;
///             trust that first.
///         </item>
///         <item>
///             Framework fast-path selectors for the SPA frameworks with
///             stable DOM signatures: MudBlazor, React (data-reactroot
///             root), Vue (data-v-app root). Tried in priority order;
///             first hit wins.
///         </item>
///         <item>
///             Existing SSR selectors (main, article, [role='main'], etc.)
///             so static documentation sites still extract via the same
///             cheap path they always did.
///         </item>
///         <item>
///             Biggest-text-container heuristic — single JS evaluation
///             that walks block-level containers, excludes nav / footer /
///             sidebar / dialog chrome, and returns the deepest node with
///             the longest non-link text. Language-agnostic safety net.
///         </item>
///     </list>
///     <para>
///         Framework strategy summary (which path each framework uses
///         when no user selector is supplied):
///         <br />Fast-path: Blazor WASM (MudBlazor variant), React CSR
///         via <c>[data-reactroot]</c>, Vue CSR via <c>[data-v-app]</c>,
///         Next.js CSR via <c>#__next</c>, and the generic <c>#app</c>
///         shell.
///         <br />Heuristic only: Angular CSR, SvelteKit CSR, Nuxt CSR —
///         their DOM patterns vary too widely across apps for a reliable
///         fast-path selector (named outlets, nested lazy-loaded modules,
///         custom shell components).
///     </para>
/// </summary>
public static class SpaAwareContentExtractor
{
    public static async Task<string> ExtractAsync(IPage page,
                                                  string? waitForSelector,
                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ct.ThrowIfCancellationRequested();

        string result = string.Empty;

        if (!string.IsNullOrEmpty(waitForSelector))
            result = await TrySelectorAsync(page, waitForSelector);

        if (string.IsNullOrEmpty(result))
            result = await TryFrameworkSelectorsAsync(page);

        if (string.IsNullOrEmpty(result))
            result = await TryStandardSelectorsAsync(page);

        if (string.IsNullOrEmpty(result))
            result = await TryBiggestContainerAsync(page);

        return result;
    }

    private static async Task<string> TrySelectorAsync(IPage page, string selector)
    {
        string result = string.Empty;
        try
        {
            var element = await page.QuerySelectorAsync(selector);
            if (element != null)
            {
                string text = await element.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                    result = text.Trim();
            }
        }
        catch(PlaywrightException)
        {
            // selector may be invalid or element detached
        }

        return result;
    }

    private static async Task<string> TryFrameworkSelectorsAsync(IPage page)
    {
        string result = string.Empty;
        foreach(string selector in smFrameworkSelectors.Where(_ => result == string.Empty))
            result = await TrySelectorAsync(page, selector);
        return result;
    }

    private static async Task<string> TryStandardSelectorsAsync(IPage page)
    {
        string result = string.Empty;
        foreach(string selector in smStandardSelectors.Where(_ => result == string.Empty))
            result = await TrySelectorAsync(page, selector);
        return result;
    }

    private static async Task<string> TryBiggestContainerAsync(IPage page)
    {
        string result = string.Empty;
        try
        {
            string text = await page.EvaluateAsync<string>(BiggestContainerScript);
            if (!string.IsNullOrWhiteSpace(text))
                result = text.Trim();
        }
        catch(PlaywrightException)
        {
            // page may have navigated away or been detached
        }

        return result;
    }

    private const string MudMainContentSelector = ".mud-main-content";
    private const string MudContainerSelector = ".mud-container";
    private const string ReactRootSelector = "[data-reactroot]";
    private const string VueAppSelector = "[data-v-app]";
    private const string NextRootSelector = "#__next";
    private const string GenericAppSelector = "#app";

    private static readonly string[] smFrameworkSelectors =
        [
            MudMainContentSelector,
            MudContainerSelector,
            ReactRootSelector,
            VueAppSelector,
            NextRootSelector,
            GenericAppSelector,
        ];

    private const string SelectorMain = "main";
    private const string SelectorArticle = "article";
    private const string SelectorRoleMain = "[role='main']";
    private const string SelectorContent = ".content";
    private const string SelectorDocContent = ".doc-content";
    private const string SelectorDocumentation = ".documentation";
    private const string SelectorIdContent = "#content";
    private const string SelectorIdMainContent = "#main-content";

    private static readonly string[] smStandardSelectors =
        [
            SelectorMain,
            SelectorArticle,
            SelectorRoleMain,
            SelectorContent,
            SelectorDocContent,
            SelectorDocumentation,
            SelectorIdContent,
            SelectorIdMainContent,
        ];

    private const string BiggestContainerScript =
        """
        (() => {
            const MIN_TEXT = 200;
            const MAX_LINK_RATIO = 0.7;
            const EXCLUDE = ['nav', 'footer', 'aside'];
            const EXCLUDE_ROLES = ['navigation', 'contentinfo', 'dialog'];
            const EXCLUDE_CLASSES = ['sidebar', 'toc', 'navbar', 'menu'];

            function isExcluded(el) {
                const tag = el.tagName ? el.tagName.toLowerCase() : '';
                if (EXCLUDE.includes(tag)) return true;
                const role = el.getAttribute && el.getAttribute('role');
                if (role && EXCLUDE_ROLES.includes(role.toLowerCase())) return true;
                const cls = (el.className || '') + '';
                for (const c of EXCLUDE_CLASSES) {
                    if (cls.toLowerCase().includes(c)) return true;
                }
                return false;
            }

            let best = null;
            let bestLen = 0;
            const candidates = document.querySelectorAll('div, section, main, article');
            for (const el of candidates) {
                if (isExcluded(el)) continue;
                const text = (el.innerText || '').trim();
                if (text.length < MIN_TEXT) continue;
                const links = el.querySelectorAll('a[href]');
                let linkLen = 0;
                for (const a of links) linkLen += (a.innerText || '').length;
                const ratio = text.length > 0 ? linkLen / text.length : 1;
                if (ratio >= MAX_LINK_RATIO) continue;
                if (text.length > bestLen) {
                    bestLen = text.length;
                    best = el;
                }
            }
            return best ? best.innerText.trim() : '';
        })()
        """;
}
