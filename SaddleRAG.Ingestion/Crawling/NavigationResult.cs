// NavigationResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Playwright;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Return value from <see cref="IPageNavigator.NavigateAsync" />.
///     Carries the navigation <see cref="IResponse" /> (so the crawler can
///     branch on HTTP status downstream) plus the raw HTTP response body
///     text (so <see cref="EscalationController" /> can shell-sniff for SPA
///     framework markers without a second Playwright round-trip).
///     <para>
///         <see cref="ResponseText" /> is never null — implementations
///         return <see cref="string.Empty" /> when the body cannot be
///         read.
///     </para>
/// </summary>
public sealed record NavigationResult
{
    public required IResponse? Response { get; init; }
    public required string ResponseText { get; init; }

    /// <summary>
    ///     Substantial-content-node count measured immediately after
    ///     <c>GotoAsync(DOMContentLoaded)</c>, before any strategy-specific
    ///     post-nav waits. The crawler measures a second count after its
    ///     own frame wait; the delta classifies SSR vs SPA. -1 when the
    ///     measurement could not be taken (e.g. response not Ok).
    /// </summary>
    public required int DomCount { get; init; }
}
