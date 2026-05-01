// DryRunPageEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single page that was visited during a dry run.
/// </summary>
public record DryRunPageEntry
{
    public required string Url { get; init; }
    public required int OutOfScopeDepth { get; init; }
    public required bool InScope { get; init; }
    public required int ContentBytes { get; init; }
    public required int LinksFound { get; init; }
}
