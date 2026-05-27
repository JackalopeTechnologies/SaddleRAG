// IPageNavigator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
///         Implementations apply their strategy-specific waits before
///         returning. The crawler still runs its own frame-waiting and
///         content-node measurement after <c>NavigateAsync</c> returns —
///         page readiness for extraction is the caller's responsibility,
///         not the navigator's.
///     </para>
/// </summary>
public interface IPageNavigator
{
    /// <summary>
    ///     Navigate <paramref name="page" /> to <paramref name="url" />,
    ///     perform strategy-specific waits, and return the navigation
    ///     response plus the raw HTTP response body text (the latter is
    ///     the input for <see cref="EscalationController" /> shell sniffing
    ///     and never null — implementations return
    ///     <see cref="string.Empty" /> when the body cannot be read).
    /// </summary>
    Task<NavigationResult> NavigateAsync(IPage page, string url, CancellationToken ct);
}
