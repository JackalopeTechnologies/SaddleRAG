// IBackgroundJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Data access for generic background job tracking records.
/// </summary>
public interface IBackgroundJobRepository
{
    /// <summary>
    ///     Create or update a job record.
    /// </summary>
    Task UpsertAsync(BackgroundJobRecord job, CancellationToken ct = default);

    /// <summary>
    ///     Get a job by id. Returns null when no record with that id exists.
    /// </summary>
    Task<BackgroundJobRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    ///     List recent jobs, most recent first. Optionally filter by job type.
    /// </summary>
    Task<IReadOnlyList<BackgroundJobRecord>> ListRecentAsync(string? jobType = null,
                                                             int limit = 20,
                                                             CancellationToken ct = default);
}
