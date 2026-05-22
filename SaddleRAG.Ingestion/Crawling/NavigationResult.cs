// NavigationResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
}
