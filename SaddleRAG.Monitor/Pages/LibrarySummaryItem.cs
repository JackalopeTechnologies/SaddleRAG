// LibrarySummaryItem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Minimal summary shown in the library card grid.
/// </summary>
public sealed record LibrarySummaryItem
{
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required int ChunkCount { get; init; }
    public required int PageCount { get; init; }
    public required bool IsSuspect { get; init; }
    public IReadOnlyList<string> SuspectReasons { get; init; } = Array.Empty<string>();
    public DateTime? LastScrapedAt { get; init; }
    public string? Hint { get; init; }
}
