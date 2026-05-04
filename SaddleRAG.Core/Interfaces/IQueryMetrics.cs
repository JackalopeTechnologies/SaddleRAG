// IQueryMetrics.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
    ///     Append a single query sample to the in-memory ring buffer.
    /// </summary>
    void Record(string operation, TimeSpan duration, bool success, int? resultCount = null, string? note = null);

    /// <summary>
    ///     Return a snapshot of recent samples plus per-operation aggregate stats.
    /// </summary>
    QueryMetricsSnapshot Snapshot();

    /// <summary>
    ///     UTC timestamp when the recorder was constructed (process-start proxy).
    /// </summary>
    DateTime ProcessStartedUtc { get; }
}
