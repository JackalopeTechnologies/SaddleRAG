// ScrapeJobRepository.cs
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
///     MongoDB implementation of scrape job tracking.
/// </summary>
public class ScrapeJobRepository : IScrapeJobRepository
{
    public ScrapeJobRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertAsync(ScrapeJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await mContext.ScrapeJobs.ReplaceOneAsync(j => j.Id == job.Id,
                                                  job,
                                                  new ReplaceOptions { IsUpsert = true },
                                                  ct
                                                 );
    }

    /// <inheritdoc />
    public async Task<ScrapeJobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var result = await mContext.ScrapeJobs
                                   .Find(j => j.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListRecentAsync(int limit = 20,
                                                                      CancellationToken ct = default)
    {
        var results = await mContext.ScrapeJobs
                                    .Find(FilterDefinition<ScrapeJobRecord>.Empty)
                                    .SortByDescending(j => j.CreatedAt)
                                    .Limit(limit)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListRunningJobsAsync(CancellationToken ct = default)
    {
        var filter = Builders<ScrapeJobRecord>.Filter.Eq(j => j.Status, ScrapeJobStatus.Running);
        var results = await mContext.ScrapeJobs.Find(filter)
                                    .SortByDescending(j => j.CreatedAt)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListActiveJobsAsync(string libraryId,
                                                                          string version,
                                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter =
            Builders<ScrapeJobRecord>.Filter.And(Builders<ScrapeJobRecord>.Filter.Eq(JobLibraryIdPath, libraryId),
                                                 Builders<ScrapeJobRecord>.Filter.Eq(JobVersionPath, version),
                                                 Builders<ScrapeJobRecord>.Filter.In(j => j.Status,
                                                          new[] { ScrapeJobStatus.Queued, ScrapeJobStatus.Running }
                                                     )
                                                );
        var results = await mContext.ScrapeJobs.Find(filter)
                                    .SortByDescending(j => j.CreatedAt)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId,
                                                          string version,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var candidates = await ListActiveJobsAsync(libraryId, version, ct);
        var staleCutoff = DateTime.UtcNow - ScrapeJobThresholds.StaleRunning;
        var result = candidates.FirstOrDefault(j => !ScrapeJobThresholds.IsStaleRunning(j, staleCutoff));
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var result = await mContext.ScrapeJobs.DeleteOneAsync(j => j.Id == id, ct);
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
            var deletion = await mContext.ScrapeJobs.DeleteManyAsync(filter, ct);
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
            result = await mContext.ScrapeJobs.CountDocumentsAsync(filter, cancellationToken: ct);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
        string? libraryId,
        string? version,
        int limit,
        CancellationToken ct = default)
    {
        var filter = BuildDeleteFilter(status, libraryId, version);
        IReadOnlyList<ScrapeJobRecord> result = Array.Empty<ScrapeJobRecord>();
        if (filter != null)
        {
            result = await mContext.ScrapeJobs.Find(filter)
                                   .SortByDescending(j => j.CreatedAt)
                                   .Limit(limit > 0 ? limit : DefaultCandidateLimit)
                                   .ToListAsync(ct);
        }

        return result;
    }

    private static FilterDefinition<ScrapeJobRecord>? BuildDeleteFilter(ScrapeJobStatus? status,
                                                                        string? libraryId,
                                                                        string? version)
    {
        var clauses = new List<FilterDefinition<ScrapeJobRecord>>();
        if (status.HasValue)
            clauses.Add(Builders<ScrapeJobRecord>.Filter.Eq(j => j.Status, status.Value));
        if (!string.IsNullOrEmpty(libraryId))
            clauses.Add(Builders<ScrapeJobRecord>.Filter.Eq(JobLibraryIdPath, libraryId));
        if (!string.IsNullOrEmpty(version))
            clauses.Add(Builders<ScrapeJobRecord>.Filter.Eq(JobVersionPath, version));

        var result = clauses.Count == 0
                         ? null
                         : Builders<ScrapeJobRecord>.Filter.And(clauses);
        return result;
    }

    private const string JobLibraryIdPath = "Job.LibraryId";
    private const string JobVersionPath = "Job.Version";
    private const int DefaultCandidateLimit = 20;
}
