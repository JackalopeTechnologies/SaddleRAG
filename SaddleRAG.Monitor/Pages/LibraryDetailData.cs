// LibraryDetailData.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models;

#endregion

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
    public IReadOnlyList<string> SuspectReasons { get; init; } = [];
    public DateTime? LastScrapedAt { get; init; }
    public DateTime? LastSuspectEvaluatedAt { get; init; }
    public double? BoundaryIssuePct { get; init; }
    public string? EmbeddingProviderId { get; init; }
    public string? EmbeddingModelName { get; init; }
    public string? ClassifierBackend { get; init; }
    public string? ClassifierModel { get; init; }
    public IReadOnlyList<HostBucket> HostnameDistribution { get; init; } = [];
    public IReadOnlyDictionary<string, double> LanguageMix { get; init; } = new Dictionary<string, double>();
}
