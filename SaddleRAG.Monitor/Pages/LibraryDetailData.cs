// LibraryDetailData.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Detailed view model for a single library shown on the library detail page.
/// </summary>
public sealed record LibraryDetailData
{
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required int ChunkCount { get; init; }
    public required int PageCount { get; init; }
    public required bool IsSuspect { get; init; }
    public string Hint { get; init; } = string.Empty;
    public IReadOnlyList<string> SuspectReasons { get; init; } = Array.Empty<string>();
    public DateTime? LastScrapedAt { get; init; }
    public DateTime? LastSuspectEvaluatedAt { get; init; }
    public double? BoundaryIssuePct { get; init; }
    public string? EmbeddingProviderId { get; init; }
    public string? EmbeddingModelName { get; init; }
}
