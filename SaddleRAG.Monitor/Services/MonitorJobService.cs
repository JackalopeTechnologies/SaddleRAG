// MonitorJobService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Read-side service for the /monitor/jobs index page. Wraps
///     <see cref="IScrapeJobRepository.ListRecentAsync" /> and projects
///     <see cref="SaddleRAG.Core.Models.ScrapeJobRecord" /> into a UI-friendly
///     row shape with optional status and library-substring filters.
/// </summary>
public sealed class MonitorJobService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorJobService" />.
    /// </summary>
    public MonitorJobService(IScrapeJobRepository jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        mJobs = jobs;
    }

    private readonly IScrapeJobRepository mJobs;

    /// <summary>
    ///     UI-friendly row projected from <see cref="SaddleRAG.Core.Models.ScrapeJobRecord" />
    ///     for the /monitor/jobs index page.
    /// </summary>
    public sealed record JobHistoryRow
    {
        /// <summary>
        ///     Unique job identifier.
        /// </summary>
        public required string JobId { get; init; }

        /// <summary>
        ///     Library this job is scraping.
        /// </summary>
        public required string LibraryId { get; init; }

        /// <summary>
        ///     Library version string.
        /// </summary>
        public required string Version { get; init; }

        /// <summary>
        ///     Stringified <see cref="ScrapeJobStatus" />.
        /// </summary>
        public required string Status { get; init; }

        /// <summary>
        ///     UTC timestamp when the job record was created.
        /// </summary>
        public required DateTime CreatedAt { get; init; }

        /// <summary>
        ///     UTC timestamp when the job started running.
        /// </summary>
        public DateTime? StartedAt { get; init; }

        /// <summary>
        ///     UTC timestamp when the job finished (success, failure, or cancellation).
        /// </summary>
        public DateTime? CompletedAt { get; init; }

        /// <summary>
        ///     Pages fully indexed (all chunks searchable).
        /// </summary>
        public int IndexedPageCount { get; init; }

        /// <summary>
        ///     Non-fatal error count across all stages.
        /// </summary>
        public int ErrorCount { get; init; }

        /// <summary>
        ///     Error message when the job failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        ///     Wall-clock duration since <see cref="StartedAt" />. Null if the
        ///     job has not started; uses <see cref="DateTime.UtcNow" /> when
        ///     the job has started but not completed.
        /// </summary>
        public TimeSpan? Duration => StartedAt is null
                                         ? null
                                         : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;
    }

    /// <summary>
    ///     Lists recent jobs, applying optional status and library-substring
    ///     (case-insensitive) filters. Results are projected to <see cref="JobHistoryRow" />
    ///     and capped at <paramref name="limit" />.
    /// </summary>
    public async Task<IReadOnlyList<JobHistoryRow>> ListAsync(ScrapeJobStatus? status = null,
                                                              string? libraryIdFilter = null,
                                                              int limit = DefaultLimit,
                                                              CancellationToken ct = default)
    {
        var fetchLimit = limit > 0 ? Math.Max(limit * 2, limit) : DefaultLimit;
        var raw = await mJobs.ListRecentAsync(fetchLimit, ct);
        var takeLimit = limit > 0 ? limit : DefaultLimit;
        var filtered = raw.Where(r => status is null || r.Status == status)
                          .Where(r => string.IsNullOrEmpty(libraryIdFilter)
                                   || r.Job.LibraryId.Contains(libraryIdFilter,
                                                               StringComparison.OrdinalIgnoreCase))
                          .Take(takeLimit)
                          .Select(r => new JobHistoryRow
                                           {
                                               JobId            = r.Id,
                                               LibraryId        = r.Job.LibraryId,
                                               Version          = r.Job.Version,
                                               Status           = r.Status.ToString(),
                                               CreatedAt        = r.CreatedAt,
                                               StartedAt        = r.StartedAt,
                                               CompletedAt      = r.CompletedAt,
                                               IndexedPageCount = r.PagesCompleted,
                                               ErrorCount       = r.ErrorCount,
                                               ErrorMessage     = r.ErrorMessage
                                           });
        return filtered.ToList();
    }

    private const int DefaultLimit = 100;
}
