// IQueryMetrics.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     In-memory recorder of per-query performance samples since process start.
///     Resets when the process restarts; not persisted.
/// </summary>
public interface IQueryMetrics
{
    /// <summary>
    ///     UTC timestamp when the recorder was constructed (process-start proxy).
    /// </summary>
    DateTime ProcessStartedUtc { get; }

    /// <summary>
    ///     Append a single query sample to the in-memory ring buffer.
    /// </summary>
    void Record(string operation,
                TimeSpan duration,
                bool success,
                int? resultCount = null,
                string? note = null);

    /// <summary>
    ///     Return a snapshot of recent samples plus per-operation aggregate stats.
    /// </summary>
    QueryMetricsSnapshot Snapshot();
}
