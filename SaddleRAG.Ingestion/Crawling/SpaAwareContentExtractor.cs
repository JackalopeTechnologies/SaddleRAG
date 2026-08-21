// SpaAwareContentExtractor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Content extractor with SPA-aware selector priority, guarded by a dominance gate so a
///     small framework-hydrated widget can never masquerade as the whole page. Strategy:
///     <list type="number">
///         <item>
///             User-supplied <c>waitForSelector</c> (when non-null) — the power-user override.
///             They asserted where the content is, so it is returned verbatim, ungated.
///         </item>
///         <item>
///             The biggest-text-container heuristic runs first as the page's content
///             <em>baseline</em>: both the dominance reference every fast-path match is
///             measured against and the fallback when no gated selector qualifies. It strips
///             nav / footer / sidebar / dialog chrome and boilerplate widgets from the text it
///             returns.
///         </item>
///         <item>
///             Framework fast-path selectors (MudBlazor, React <c>[data-reactroot]</c>, Vue
///             <c>[data-v-app]</c>, Next.js <c>#__next</c>, generic <c>#app</c>) and SSR
///             selectors (<c>main</c>, <c>article</c>, <c>[role='main']</c>, then generic
///             content classes) are tried in priority order. A match is admitted only when it
///             is not a boilerplate widget and holds a dominant share of the baseline's text —
///             lenient for semantic landmarks and framework roots, strict for the ambiguous
///             generic content classes.
///         </item>
///     </list>
///     <para>
///         The dominance gate replaces the earlier enclosure probe, which could only demote a
///         widget that a standard SSR container happened to enclose. On a landmark-free page
///         (no <c>&lt;main&gt;</c>/<c>&lt;article&gt;</c>/<c>.content</c>) a Vue-mounted rating
///         box carries <c>data-v-app</c> and enclosed nothing, so the probe let it win. Measured
///         against the baseline it is plainly sub-dominant, so the gate rejects it.
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

        // 1. User-supplied selector — the power-user override. Returned verbatim, ungated by
        //    the widget and dominance checks below.
        string result = string.Empty;
        if (!string.IsNullOrEmpty(waitForSelector))
            result = await TrySelectorTextAsync(page, waitForSelector);

        // 2/3/4. Otherwise compute the content baseline and pick the first gated selector that
        //        beats it, falling back to the baseline itself.
        if (string.IsNullOrEmpty(result))
        {
            string baseline = await ComputeBaselineAsync(page);
            result = await SelectDominantAsync(page, baseline);
        }

        return result;
    }

    private static async Task<string> SelectDominantAsync(IPage page, string baseline)
    {
        string result = string.Empty;
        int baselineLength = baseline.Length;

        foreach(GatedSelector candidate in smGatedSelectors.Where(_ => result == string.Empty))
        {
            IElementHandle? element = await TrySelectorHandleAsync(page, candidate.Selector);
            if (element is not null)
            {
                string text = await TryInnerTextAsync(element);
                bool accepted = !string.IsNullOrEmpty(text) &&
                                !await IsWidgetSignpostedAsync(element, text) &&
                                (baselineLength == 0 || text.Length >= baselineLength * candidate.DominanceRatio);
                if (accepted)
                    result = text;
            }
        }

        if (result == string.Empty)
            result = baseline;

        return result;
    }

    private static async Task<string> TrySelectorTextAsync(IPage page, string selector)
    {
        IElementHandle? element = await TrySelectorHandleAsync(page, selector);
        string result = element is null ? string.Empty : await TryInnerTextAsync(element);
        return result;
    }

    private static async Task<IElementHandle?> TrySelectorHandleAsync(IPage page, string selector)
    {
        IElementHandle? result = null;
        try
        {
            result = await page.QuerySelectorAsync(selector);
        }
        catch(PlaywrightException)
        {
            // selector may be invalid or the element detached
        }

        return result;
    }

    private static async Task<string> TryInnerTextAsync(IElementHandle element)
    {
        string result = string.Empty;
        try
        {
            string text = await element.InnerTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                result = text.Trim();
        }
        catch(PlaywrightException)
        {
            // element may have detached between selection and read
        }

        return result;
    }

    /// <summary>
    ///     Runs the biggest-text-container heuristic and returns the winner's text with chrome
    ///     and boilerplate-widget subtrees stripped. Returns empty when the page cannot be
    ///     evaluated, in which case selector matches stand ungated rather than the page silently
    ///     losing its content.
    /// </summary>
    private static async Task<string> ComputeBaselineAsync(IPage page)
    {
        string result = string.Empty;
        try
        {
            string text = await page.EvaluateAsync<string>(BaselineContentScript,
                                                           new { excludeTokens = smBaselineExcludeTokens }
                                                          );
            if (!string.IsNullOrWhiteSpace(text))
                result = text.Trim();
        }
        catch(PlaywrightException)
        {
            // an un-evaluable page yields no baseline; selector matches then stand ungated
        }

        return result;
    }

    /// <summary>
    ///     True when the matched element reads as a boilerplate widget rather than main content:
    ///     an id/class token in the widget denylist (language-tolerant, since these are technical
    ///     component names), or structurally a small vote form (a radio/checkbox control with
    ///     little accompanying text). An un-inspectable element is treated as content, matching
    ///     the fast path's bias toward keeping a match it cannot positively disqualify.
    /// </summary>
    private static async Task<bool> IsWidgetSignpostedAsync(IElementHandle element, string text)
    {
        bool result = false;
        try
        {
            result = HasWidgetToken(await element.GetAttributeAsync(IdAttribute)) ||
                     HasWidgetToken(await element.GetAttributeAsync(ClassAttribute));

            if (!result && text.Length < WidgetFormMaxTextLength)
            {
                IElementHandle? voteControl = await element.QuerySelectorAsync(VoteControlSelector);
                result = voteControl is not null;
            }
        }
        catch(PlaywrightException)
        {
            // an un-inspectable element is treated as content, not a widget
        }

        return result;
    }

    private static bool HasWidgetToken(string? attributeValue)
    {
        bool result = false;
        if (!string.IsNullOrEmpty(attributeValue))
        {
            string[] tokens = attributeValue.Split(smTokenSeparators,
                                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach(string token in tokens.Where(_ => !result))
                result = smWidgetTokenSet.Contains(token.ToLowerInvariant());
        }

        return result;
    }

    private readonly record struct GatedSelector(string Selector, double DominanceRatio);

    // A semantic landmark or a genuine SPA shell holds ~all the page text, so trust it when it
    // reaches even a small fraction of the baseline. A generic content class easily matches a
    // widget subtree, so hold it to half the baseline.
    private const double LenientDominanceRatio = 0.15;
    private const double StrictDominanceRatio = 0.5;

    private const string MudMainContentSelector = ".mud-main-content";
    private const string MudContainerSelector = ".mud-container";
    private const string ReactRootSelector = "[data-reactroot]";
    private const string VueAppSelector = "[data-v-app]";
    private const string NextRootSelector = "#__next";
    private const string GenericAppSelector = "#app";
    private const string SelectorMain = "main";
    private const string SelectorArticle = "article";
    private const string SelectorRoleMain = "[role='main']";
    private const string SelectorContent = ".content";
    private const string SelectorDocContent = ".doc-content";
    private const string SelectorDocumentation = ".documentation";
    private const string SelectorIdContent = "#content";
    private const string SelectorIdMainContent = "#main-content";

    private static readonly GatedSelector[] smGatedSelectors =
        [
            new(MudMainContentSelector, LenientDominanceRatio),
            new(MudContainerSelector, LenientDominanceRatio),
            new(ReactRootSelector, LenientDominanceRatio),
            new(VueAppSelector, LenientDominanceRatio),
            new(NextRootSelector, LenientDominanceRatio),
            new(GenericAppSelector, LenientDominanceRatio),
            new(SelectorMain, LenientDominanceRatio),
            new(SelectorArticle, LenientDominanceRatio),
            new(SelectorRoleMain, LenientDominanceRatio),
            new(SelectorContent, StrictDominanceRatio),
            new(SelectorDocContent, StrictDominanceRatio),
            new(SelectorDocumentation, StrictDominanceRatio),
            new(SelectorIdContent, StrictDominanceRatio),
            new(SelectorIdMainContent, StrictDominanceRatio),
        ];

    private const int WidgetFormMaxTextLength = 400;
    private const string VoteControlSelector = "input[type=radio], input[type=checkbox]";
    private const string IdAttribute = "id";
    private const string ClassAttribute = "class";

    // Widget/boilerplate id/class tokens used to reject a signposted selector match. These are
    // technical component names, not English prose, so they are language-tolerant: a French or
    // Japanese site still names its element rating-component / feedback / disqus.
    private const string WidgetTokenListA = "rating feedback helpful survey poll";
    private const string WidgetTokenListB = "share social breadcrumb breadcrumbs newsletter";
    private const string WidgetTokenListC = "subscribe cookie consent comments disqus";
    private const string WidgetTokenListD = "related promo banner cta gdpr";

    // Nav-chrome tokens the heuristic has always excluded (now id-aware, closing a class-only
    // blind spot). Combined with the widget tokens for the baseline script.
    private const string ChromeTokenList = "sidebar toc navbar menu";

    private const char TokenListSeparator = ' ';

    private static readonly char[] smTokenSeparators = [' ', '\t', '\n', '\r', '-', '_'];

    private static readonly string[] smWidgetTokens =
        [
            .. WidgetTokenListA.Split(TokenListSeparator),
            .. WidgetTokenListB.Split(TokenListSeparator),
            .. WidgetTokenListC.Split(TokenListSeparator),
            .. WidgetTokenListD.Split(TokenListSeparator),
        ];

    private static readonly HashSet<string> smWidgetTokenSet = new(smWidgetTokens, StringComparer.Ordinal);

    private static readonly string[] smBaselineExcludeTokens =
        [.. ChromeTokenList.Split(TokenListSeparator), .. smWidgetTokens];

    private const string BaselineContentScript =
        """
        (args) => {
            const MIN_TEXT = 200;
            const MAX_LINK_RATIO = 0.7;
            const EXCLUDE_TAGS = ['nav', 'footer', 'aside'];
            const EXCLUDE_ROLES = ['navigation', 'contentinfo', 'dialog'];
            const EXCLUDE_TOKENS = args.excludeTokens || [];

            function tokens(value) {
                return (value || '').toString().toLowerCase().split(/[\s_-]+/).filter(Boolean);
            }

            function isExcluded(el) {
                const tag = el.tagName ? el.tagName.toLowerCase() : '';
                if (EXCLUDE_TAGS.includes(tag)) return true;
                const role = el.getAttribute && el.getAttribute('role');
                if (role && EXCLUDE_ROLES.includes(role.toLowerCase())) return true;
                const toks = tokens(el.id).concat(tokens(el.getAttribute && el.getAttribute('class')));
                for (const t of toks) {
                    if (EXCLUDE_TOKENS.includes(t)) return true;
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

            if (!best) return '';

            // Return the winner's text with chrome/widget subtrees removed. The clone is appended
            // off-screen so innerText has layout to work from, then discarded — the live DOM is
            // left intact so downstream link discovery still sees the navigation.
            const clone = best.cloneNode(true);
            for (const el of Array.from(clone.querySelectorAll('*'))) {
                if (isExcluded(el)) el.remove();
            }
            clone.style.position = 'absolute';
            clone.style.left = '-99999px';
            clone.style.top = '0';
            clone.setAttribute('aria-hidden', 'true');
            document.body.appendChild(clone);
            const cleaned = (clone.innerText || '').trim();
            clone.remove();
            return cleaned || best.innerText.trim();
        }
        """;
}
