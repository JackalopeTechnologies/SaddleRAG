// PageDiffEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     A page that was added or removed between versions.
/// </summary>
public record PageDiffEntry
{
    /// <summary>
    ///     Page URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Page title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    ///     Page classification category.
    /// </summary>
    public required DocCategory Category { get; init; }
}
