// IScrapeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Data access for scrape job tracking records.
/// </summary>
public interface IScrapeJobRepository
{
    /// <summary>
    ///     Create or update a job record.
    /// </summary>
    Task UpsertAsync(ScrapeJobRecord job, CancellationToken ct = default);

    /// <summary>
    ///     Get a job by id.
    /// </summary>
    Task<ScrapeJobRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     List recent jobs (most recent first).
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    ///     List every job currently in <see cref="SaddleRAG.Core.Enums.ScrapeJobStatus.Running" />
    ///     across all libraries, sorted by CreatedAt descending. Used by
    ///     get_dashboard_index to surface orphan Running jobs that fall
    ///     outside the recent-jobs window.
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListRunningJobsAsync(CancellationToken ct = default);

    /// <summary>
    ///     List every Queued or Running job for (libraryId, version),
    ///     sorted by CreatedAt descending. Used by submit_url_correction
    ///     to cancel parallel in-flight work on the same library/version
    ///     before re-queuing at a corrected URL.
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListActiveJobsAsync(string libraryId,
                                                             string version,
                                                             CancellationToken ct = default);

    /// <summary>
    ///     Return the most-recently-created Queued or non-stale Running
    ///     job for (libraryId, version), or null if none exists. Stale
    ///     Running jobs (orphans whose effective progress timestamp is
    ///     older than <see cref="ScrapeJobThresholds.StaleRunning" />) are
    ///     skipped so the start_ingest state machine can move forward
    ///     instead of getting wedged on a dead runner.
    /// </summary>
    Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Delete a single scrape job record by id. Returns true if a row
    ///     was removed, false if no row matched.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     Delete every scrape job matching the supplied filter. At least
    ///     one filter (status, libraryId, or version) must be specified;
    ///     callers that pass no filter receive zero deletions to prevent
    ///     accidental wholesale removal. Returns the number of rows removed.
    /// </summary>
    Task<long> DeleteManyAsync(ScrapeJobStatus? status,
                               string? libraryId,
                               string? version,
                               CancellationToken ct = default);

    /// <summary>
    ///     Count the rows that <see cref="DeleteManyAsync" /> would delete
    ///     for the supplied filter. Returns zero when no filter is set so
    ///     dry-run preview matches the apply behaviour.
    /// </summary>
    Task<long> CountDeleteCandidatesAsync(ScrapeJobStatus? status,
                                          string? libraryId,
                                          string? version,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Return the rows that <see cref="DeleteManyAsync" /> would delete
    ///     for the supplied filter, ordered most-recent first and capped at
    ///     <paramref name="limit" />. Used by dry-run previews so callers
    ///     can show a sample of affected job ids.
    /// </summary>
    Task<IReadOnlyList<ScrapeJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                                   string? libraryId,
                                                                   string? version,
                                                                   int limit,
                                                                   CancellationToken ct = default);
}
