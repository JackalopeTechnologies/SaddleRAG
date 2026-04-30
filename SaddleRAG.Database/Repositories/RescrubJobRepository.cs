// RescrubJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using MongoDB.Driver;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of rescrub job tracking.
/// </summary>
public class RescrubJobRepository : IRescrubJobRepository
{
    public RescrubJobRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertAsync(RescrubJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await mContext.RescrubJobs.ReplaceOneAsync(j => j.Id == job.Id,
                                                   job,
                                                   new ReplaceOptions { IsUpsert = true },
                                                   ct
                                                  );
    }

    /// <inheritdoc />
    public async Task<RescrubJobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var result = await mContext.RescrubJobs
                                    .Find(j => j.Id == id)
                                    .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RescrubJobRecord>> ListRecentAsync(int limit = 20,
                                                                        CancellationToken ct = default)
    {
        var results = await mContext.RescrubJobs
                                     .Find(FilterDefinition<RescrubJobRecord>.Empty)
                                     .SortByDescending(j => j.CreatedAt)
                                     .Limit(limit)
                                     .ToListAsync(ct);
        return results;
    }
}
