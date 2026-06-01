// QueryMetricsSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryMetricsSnapshot
{
    public required DateTime ProcessStartedUtc { get; init; }
    public required IReadOnlyList<QuerySample> RecentSamples { get; init; }
    public required IReadOnlyList<QueryOperationStats> PerOperation { get; init; }
}
