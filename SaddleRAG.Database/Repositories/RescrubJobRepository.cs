// RescrubJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

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

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var result = await mContext.RescrubJobs.DeleteOneAsync(j => j.Id == id, ct);
        return result.DeletedCount > 0;
    }

    /// <inheritdoc />
    public async Task<long> DeleteManyAsync(ScrapeJobStatus? status,
                                            string? libraryId,
                                            string? version,
                                            CancellationToken ct = default)
    {
        var filter = BuildDeleteFilter(status, libraryId, version);
        long result = 0;
        if (filter != null)
        {
            var deletion = await mContext.RescrubJobs.DeleteManyAsync(filter, ct);
            result = deletion.DeletedCount;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<long> CountDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                       string? libraryId,
                                                       string? version,
                                                       CancellationToken ct = default)
    {
        var filter = BuildDeleteFilter(status, libraryId, version);
        long result = 0;
        if (filter != null)
            result = await mContext.RescrubJobs.CountDocumentsAsync(filter, cancellationToken: ct);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RescrubJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                                                 string? libraryId,
                                                                                 string? version,
                                                                                 int limit,
                                                                                 CancellationToken ct = default)
    {
        var filter = BuildDeleteFilter(status, libraryId, version);
        IReadOnlyList<RescrubJobRecord> result = Array.Empty<RescrubJobRecord>();
        if (filter != null)
            result = await mContext.RescrubJobs.Find(filter)
                                   .SortByDescending(j => j.CreatedAt)
                                   .Limit(limit > 0 ? limit : DefaultCandidateLimit)
                                   .ToListAsync(ct);

        return result;
    }

    private static FilterDefinition<RescrubJobRecord>? BuildDeleteFilter(ScrapeJobStatus? status,
                                                                         string? libraryId,
                                                                         string? version)
    {
        var clauses = new List<FilterDefinition<RescrubJobRecord>>();
        if (status.HasValue)
            clauses.Add(Builders<RescrubJobRecord>.Filter.Eq(j => j.Status, status.Value));
        if (!string.IsNullOrEmpty(libraryId))
            clauses.Add(Builders<RescrubJobRecord>.Filter.Eq(j => j.LibraryId, libraryId));
        if (!string.IsNullOrEmpty(version))
            clauses.Add(Builders<RescrubJobRecord>.Filter.Eq(j => j.Version, version));

        FilterDefinition<RescrubJobRecord>? result = clauses.Count == 0
                                                         ? null
                                                         : Builders<RescrubJobRecord>.Filter.And(clauses);
        return result;
    }

    private const int DefaultCandidateLimit = 20;
}
