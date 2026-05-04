// AuditPageOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Outcome of fetching and indexing a page.
/// </summary>
public sealed record AuditPageOutcome
{
    /// <summary>
    ///     HTTP status code or fetch error label (e.g. "200", "Timeout").
    /// </summary>
    public string? FetchStatus { get; init; }

    /// <summary>
    ///     Classified documentation category of the page.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    ///     Number of chunks produced from this page.
    /// </summary>
    public int? ChunkCount { get; init; }

    /// <summary>
    ///     Error message if the page failed to process.
    /// </summary>
    public string? Error { get; init; }
}
