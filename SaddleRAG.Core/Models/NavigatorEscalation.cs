// NavigatorEscalation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Marker record for "the crawler switched to the SPA navigator mid-run".
///     A <see cref="DryRunReport" /> exposes this as
///     <see cref="DryRunReport.Escalation" />: null means the crawl finished
///     on the SSR navigator; non-null carries the human-readable reason
///     (framework signal or user-supplied selector) so operators can tell
///     at a glance what triggered the swap.
///     <para>
///         A single nullable record replaces the previous
///         <c>NavigatorEscalated</c> bool / <c>Reason</c> string pair so the
///         "reason is empty when not escalated" invariant is enforced by the
///         type rather than by convention.
///     </para>
/// </summary>
public sealed record NavigatorEscalation
{
    public required string Reason { get; init; }
}
