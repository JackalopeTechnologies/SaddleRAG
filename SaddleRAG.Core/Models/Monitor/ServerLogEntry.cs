// ServerLogEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     One parsed server log entry: the timestamped first line plus any
///     continuation lines (typically an exception with stack trace).
/// </summary>
public sealed record ServerLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required ServerLogLevel Level { get; init; }
    public required string Message { get; init; }
    public required IReadOnlyList<string> DetailLines { get; init; }
}
