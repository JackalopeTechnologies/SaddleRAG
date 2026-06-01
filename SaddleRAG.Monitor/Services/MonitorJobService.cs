// MonitorJobService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Read-side service for the /monitor/jobs index page. Wraps
///     <see cref="IUnifiedJobView" /> and projects <see cref="JobRow" />
///     into the page-friendly <see cref="JobHistoryRow" /> shape.
/// </summary>
public sealed class MonitorJobService
{
    /// <summary>
    ///     UI-friendly row projected from <see cref="JobRow" /> for the
    ///     /monitor/jobs index page.
    /// </summary>
    public sealed record JobHistoryRow
    {
        public required string JobId { get; init; }
        public required JobType Type { get; init; }
        public string? LibraryId { get; init; }
        public string? Version { get; init; }
        public string? RenameToId { get; init; }
        public string? ScanPath { get; init; }
        public required string Status { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int ItemsProcessed { get; init; }
        public int ItemsTotal { get; init; }
        public string? ItemsLabel { get; init; }
        public int ErrorCount { get; init; }
        public string? ErrorMessage { get; init; }

        /// <summary>
        ///     Wall-clock duration since <see cref="StartedAt" />.
        /// </summary>
        public TimeSpan? Duration => StartedAt is null
                                         ? null
                                         : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
    }

    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorJobService" />.
    /// </summary>
    public MonitorJobService(IUnifiedJobView jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        mJobs = jobs;
    }

    private readonly IUnifiedJobView mJobs;

    /// <summary>
    ///     Lists recent jobs across all storage paths, projected to <see cref="JobHistoryRow" />.
    /// </summary>
    public async Task<IReadOnlyList<JobHistoryRow>> ListAsync(ScrapeJobStatus? status = null,
                                                              JobType? typeFilter = null,
                                                              string? libraryIdFilter = null,
                                                              int limit = DefaultLimit,
                                                              CancellationToken ct = default)
    {
        var rows = await mJobs.ListAsync(status, typeFilter, libraryIdFilter, limit, ct);
        return rows.Select(r => new JobHistoryRow
                                    {
                                        JobId = r.JobId,
                                        Type = r.Type,
                                        LibraryId = r.LibraryId,
                                        Version = r.Version,
                                        RenameToId = r.RenameToId,
                                        ScanPath = r.ScanPath,
                                        Status = r.Status.ToString(),
                                        CreatedAt = r.CreatedAt,
                                        StartedAt = r.StartedAt,
                                        CompletedAt = r.CompletedAt,
                                        ItemsProcessed = r.ItemsProcessed,
                                        ItemsTotal = r.ItemsTotal,
                                        ItemsLabel = r.ItemsLabel,
                                        ErrorCount = r.ErrorCount,
                                        ErrorMessage = r.ErrorMessage
                                    }
                          )
                   .ToList();
    }

    private const int DefaultLimit = 100;
}
