// JobTickSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobTickSnapshot
{
    public required string JobId { get; init; }
    public required PipelineCounters Counters { get; init; }
    public required IReadOnlyList<RecentFetch> RecentFetches { get; init; }
    public required IReadOnlyList<RecentReject> RecentRejects { get; init; }
    public required IReadOnlyList<RecentError> RecentErrors { get; init; }
    public string? CurrentHost { get; init; }
}
