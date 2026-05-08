// IRescrubJobRepository.cs
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
///     Data access for rescrub job tracking records.
/// </summary>
public interface IRescrubJobRepository
{
    /// <summary>
    ///     Create or update a job record.
    /// </summary>
    Task UpsertAsync(RescrubJobRecord job, CancellationToken ct = default);

    /// <summary>
    ///     Get a job by id.
    /// </summary>
    Task<RescrubJobRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     List recent jobs, most recent first.
    /// </summary>
    Task<IReadOnlyList<RescrubJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>
    ///     Delete a single rescrub job by id. Returns true when a row was removed.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     Delete every rescrub job matching the supplied filter. Callers
    ///     that pass no filter receive zero deletions to prevent accidental
    ///     wholesale removal. Returns the number of rows deleted.
    /// </summary>
    Task<long> DeleteManyAsync(ScrapeJobStatus? status,
                               string? libraryId,
                               string? version,
                               CancellationToken ct = default);

    /// <summary>
    ///     Count rows that <see cref="DeleteManyAsync" /> would delete.
    /// </summary>
    Task<long> CountDeleteCandidatesAsync(ScrapeJobStatus? status,
                                          string? libraryId,
                                          string? version,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Return a sample of rows that <see cref="DeleteManyAsync" /> would delete,
    ///     ordered most-recent first and capped at <paramref name="limit" />.
    /// </summary>
    Task<IReadOnlyList<RescrubJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                                    string? libraryId,
                                                                    string? version,
                                                                    int limit,
                                                                    CancellationToken ct = default);
}
