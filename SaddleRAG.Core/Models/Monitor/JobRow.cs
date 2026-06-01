// JobRow.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Common row shape returned by <see cref="SaddleRAG.Core.Interfaces.IUnifiedJobView" />.
///     Projects every job-storage type into a single display model.
/// </summary>
public sealed record JobRow
{
    public required string JobId { get; init; }
    public required JobType Type { get; init; }
    public required ScrapeJobStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    public string? LibraryId { get; init; }
    public string? Version { get; init; }
    public string? RenameToId { get; init; }
    public string? ScanPath { get; init; }

    public int ItemsProcessed { get; init; }
    public int ItemsTotal { get; init; }
    public string? ItemsLabel { get; init; }

    public int ErrorCount { get; init; }
    public string? ErrorMessage { get; init; }

    public TimeSpan? Duration =>
        StartedAt is null ? null : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;

    /// <summary>
    ///     Whether <see cref="Type" /> supports cooperative cancellation via
    ///     <c>cancel_job</c>. Surfaced so the monitor UI can hide the cancel
    ///     button for atomic mutations (deletes, renames, cleanups, etc).
    /// </summary>
    public bool IsCancellable => Type.IsCancellable();
}
