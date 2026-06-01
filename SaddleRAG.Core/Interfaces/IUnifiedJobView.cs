// IUnifiedJobView.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Read-side view that unions ScrapeJobs, BackgroundJobs, and RescrubJobs
///     into a single ordered <see cref="JobRow" /> list for the monitor UI.
/// </summary>
public interface IUnifiedJobView
{
    /// <summary>
    ///     Lists recent jobs across all storage paths, projected and filtered
    ///     to a common shape. Sorted by <see cref="JobRow.CreatedAt" /> desc.
    /// </summary>
    Task<IReadOnlyList<JobRow>> ListAsync(ScrapeJobStatus? statusFilter,
                                          JobType? typeFilter,
                                          string? libraryFilter,
                                          int limit,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Returns a single job by id from any storage path, or null if no
    ///     job with that id exists.
    /// </summary>
    Task<JobRow?> GetAsync(string jobId, CancellationToken ct = default);
}
