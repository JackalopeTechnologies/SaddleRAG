// DryRunSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Immutable snapshot of a <see cref="DryRunAccumulator" />'s state.
///     Produced by <see cref="DryRunAccumulator.Snapshot" /> after the
///     dry-run pipeline drains; the contained collections are copies of
///     the accumulator's internal storage and safe to read without
///     synchronization.
/// </summary>
public record DryRunSnapshot
{
    public required int TotalPages { get; init; }
    public required int InScopePages { get; init; }
    public required int OutOfScopePages { get; init; }
    public required int DepthLimitedSkips { get; init; }
    public required int FilteredSkips { get; init; }
    public required int FetchErrors { get; init; }
    public required IReadOnlyDictionary<string, int> PagesByHost { get; init; }
    public required IReadOnlyDictionary<int, int> DepthDistribution { get; init; }
    public required IReadOnlyList<string> GitHubRepos { get; init; }
    public required IReadOnlyDictionary<DocCategory, int> CategoryHistogram { get; init; }
    public required IReadOnlyList<DryRunPageEntry> SamplePages { get; init; }
    public required IReadOnlyList<DryRunFetchError> Errors { get; init; }
    public required RenderMode RenderMode { get; init; }
    public required int MedianContentNodeDelta { get; init; }
    public required bool LoadWaitRecommended { get; init; }
    public required StageTimings Timings { get; init; }
    public NavigatorEscalation? Escalation { get; init; }
}
