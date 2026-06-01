// IJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Persistence layer for the unified <c>jobs</c> collection.
///     Replaces the four legacy per-pipeline repositories
///     (<c>IScrapeJobRepository</c>, <c>IRescrubJobRepository</c>,
///     <c>IReembedJobRepository</c>, <c>IBackgroundJobRepository</c>);
///     callers filter by <see cref="JobType" /> instead of by repository.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    ///     Upserts a job record by <see cref="JobRecord.Id" />.
    /// </summary>
    Task UpsertAsync(JobRecord record, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves a single job record by id, or null when not found.
    /// </summary>
    Task<JobRecord?> GetAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    ///     Lists the most recently created jobs, optionally filtered by
    ///     <paramref name="jobType" />, sorted by
    ///     <see cref="JobRecord.CreatedAt" /> descending.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ListRecentAsync(JobType? jobType = null,
                                                   int limit = 20,
                                                   CancellationToken ct = default);

    /// <summary>
    ///     Lists jobs currently in <see cref="JobStatus.Running" />,
    ///     optionally filtered by <paramref name="jobType" />.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ListRunningAsync(JobType? jobType = null,
                                                    CancellationToken ct = default);

    /// <summary>
    ///     Lists jobs not yet in a terminal state
    ///     (<see cref="JobStatus.Queued" /> or
    ///     <see cref="JobStatus.Running" />) for the given library.
    ///     Pass null <paramref name="version" /> to match any version.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ListActiveAsync(string libraryId,
                                                    string? version = null,
                                                    JobType? jobType = null,
                                                    CancellationToken ct = default);

    /// <summary>
    ///     Returns the single non-terminal job for the given
    ///     <paramref name="libraryId" /> / <paramref name="version" /> /
    ///     <paramref name="jobType" /> tuple, or null when none exists.
    ///     Used by scrape orchestration to detect duplicate-queue attempts.
    /// </summary>
    Task<JobRecord?> GetActiveAsync(string libraryId,
                                     string version,
                                     JobType jobType,
                                     CancellationToken ct = default);

    /// <summary>
    ///     Deletes a single job by id. Returns true on hit.
    /// </summary>
    Task<bool> DeleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    ///     Deletes jobs matching the provided filter and returns the
    ///     count removed. Any filter argument left null is omitted from
    ///     the predicate. Passing all-null is rejected (refuses to
    ///     truncate the whole collection by accident).
    /// </summary>
    Task<long> DeleteManyAsync(JobType? jobType,
                                JobStatus? status,
                                string? libraryId,
                                string? version,
                                DateTime? completedBefore,
                                CancellationToken ct = default);

    /// <summary>
    ///     Counts jobs that would match
    ///     <see cref="DeleteManyAsync" /> with the same arguments.
    /// </summary>
    Task<long> CountDeleteCandidatesAsync(JobType? jobType,
                                           JobStatus? status,
                                           string? libraryId,
                                           string? version,
                                           DateTime? completedBefore,
                                           CancellationToken ct = default);

    /// <summary>
    ///     Lists jobs that would match
    ///     <see cref="DeleteManyAsync" /> with the same arguments,
    ///     capped at <paramref name="limit" />, sorted by
    ///     <see cref="JobRecord.CreatedAt" /> descending.
    /// </summary>
    Task<IReadOnlyList<JobRecord>> ListDeleteCandidatesAsync(JobType? jobType,
                                                              JobStatus? status,
                                                              string? libraryId,
                                                              string? version,
                                                              DateTime? completedBefore,
                                                              int limit,
                                                              CancellationToken ct = default);
}
