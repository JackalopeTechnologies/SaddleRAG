// QueryMetricsSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryMetricsSnapshot
{
    public required DateTime ProcessStartedUtc { get; init; }
    public required IReadOnlyList<QuerySample> RecentSamples { get; init; }
    public required IReadOnlyList<QueryOperationStats> PerOperation { get; init; }
}
