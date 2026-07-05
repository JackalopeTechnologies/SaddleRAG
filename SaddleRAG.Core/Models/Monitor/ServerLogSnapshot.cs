// ServerLogSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Parsed tail of the newest server log file. <see cref="Entries" /> is
///     newest-first and capped by the caller's request;
///     <see cref="TotalEntriesInWindow" /> is the uncapped count within the
///     read window. <see cref="LogFileName" /> is null when no log file exists.
/// </summary>
public sealed record ServerLogSnapshot
{
    public required string? LogFileName { get; init; }
    public required IReadOnlyList<ServerLogEntry> Entries { get; init; }
    public required int TotalEntriesInWindow { get; init; }
    public required bool TruncatedAtWindow { get; init; }

    /// <summary>
    ///     Snapshot representing "no log file found".
    /// </summary>
    public static ServerLogSnapshot Empty { get; } = new()
                                                         {
                                                             LogFileName = null,
                                                             Entries = [],
                                                             TotalEntriesInWindow = 0,
                                                             TruncatedAtWindow = false
                                                         };
}
