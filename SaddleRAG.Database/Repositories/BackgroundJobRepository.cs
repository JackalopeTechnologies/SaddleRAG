// BackgroundJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of generic background job tracking.
/// </summary>
public class BackgroundJobRepository : IBackgroundJobRepository
{
    public BackgroundJobRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertAsync(BackgroundJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await mContext.BackgroundJobs.ReplaceOneAsync(j => j.Id == job.Id,
                                                      job,
                                                      new ReplaceOptions { IsUpsert = true },
                                                      ct
                                                     );
    }

    /// <inheritdoc />
    public async Task<BackgroundJobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var result = await mContext.BackgroundJobs
                                   .Find(j => j.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackgroundJobRecord>> ListRecentAsync(string? jobType = null,
                                                                          int limit = 20,
                                                                          CancellationToken ct = default)
    {
        var filter = jobType is null
                         ? FilterDefinition<BackgroundJobRecord>.Empty
                         : Builders<BackgroundJobRecord>.Filter.Eq(j => j.JobType, jobType);

        var results = await mContext.BackgroundJobs
                                    .Find(filter)
                                    .SortByDescending(j => j.CreatedAt)
                                    .Limit(limit)
                                    .ToListAsync(ct);
        return results;
    }
}
