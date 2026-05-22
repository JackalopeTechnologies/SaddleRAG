// IPageNavigator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Strategy for navigating to a documentation page with Playwright.
///     Two implementations: <see cref="SsrPageNavigator" /> (server-side
///     rendered sites — DOMContentLoaded only) and <see cref="SpaPageNavigator" />
///     (single-page application sites — NetworkIdle + settle + optional
///     selector wait, used after <c>EscalationController</c> detects an SPA
///     shell or when the caller supplied an explicit <c>WaitForSelector</c>).
///     <para>
///         Returns the navigation <see cref="IResponse" /> so the caller
///         can branch on HTTP status, plus the raw response body text so
///         the escalation controller can sniff for SPA framework markers
///         without an extra Playwright round-trip. Implementations leave
///         <paramref name="page" /> in a state where
///         <c>page.ContentAsync()</c> reflects the fully hydrated DOM.
///     </para>
/// </summary>
public interface IPageNavigator
{
    /// <summary>
    ///     Navigate <paramref name="page" /> to <paramref name="url" />,
    ///     perform any strategy-specific waits, and return the response
    ///     and the raw HTTP response body text.
    /// </summary>
    Task<(IResponse? Response, string ResponseText)> NavigateAsync(IPage page,
                                                                   string url,
                                                                   CancellationToken ct);
}
