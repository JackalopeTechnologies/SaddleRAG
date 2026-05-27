// QueryOperationStats.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryOperationStats
{
    public required string Operation { get; init; }
    public required int Count { get; init; }
    public required int FailureCount { get; init; }
    public required double AvgMs { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
    public required double MaxMs { get; init; }
}
