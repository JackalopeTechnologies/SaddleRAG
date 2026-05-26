// JobTickEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobTickEvent
{
    public required string JobId { get; init; }
    public required DateTime At { get; init; }
    public required PipelineCounters Counters { get; init; }
    public string? CurrentHost { get; init; }
    public IReadOnlyList<RecentFetch> RecentFetches { get; init; } = [];
    public IReadOnlyList<RecentReject> RecentRejects { get; init; } = [];
    public IReadOnlyList<RecentError> ErrorsThisTick { get; init; } = [];
}
