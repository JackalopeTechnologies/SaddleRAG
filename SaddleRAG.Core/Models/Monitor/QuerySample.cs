// QuerySample.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QuerySample
{
    public required DateTime At { get; init; }
    public required string Operation { get; init; }
    public required double DurationMs { get; init; }
    public required bool Success { get; init; }
    public int? ResultCount { get; init; }
    public string? Note { get; init; }
}
