// JobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of <see cref="IJobRepository" /> backed by
///     the unified <c>jobs</c> collection. <see cref="JobType" /> and
///     <see cref="JobStatus" /> are persisted as strings via class-map
///     registration in <see cref="SaddleRagDbContext" />.
/// </summary>
public sealed class JobRepository : IJobRepository
{
    public JobRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task UpsertAsync(JobRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await mContext.Jobs.ReplaceOneAsync(j => j.Id == record.Id,
                                            record,
                                            new ReplaceOptions { IsUpsert = true },
                                            ct
                                           );
    }

    /// <inheritdoc />
    public async Task<JobRecord?> GetAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobRecord? result = await mContext.Jobs
                                          .Find(j => j.Id == jobId)
                                          .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> ListRecentAsync(JobType? jobType = null,
                                                                 int limit = 20,
                                                                 CancellationToken ct = default)
    {
        FilterDefinition<JobRecord> filter = jobType is null
            ? Builders<JobRecord>.Filter.Empty
            : Builders<JobRecord>.Filter.Eq(j => j.JobType, jobType.Value);

        IReadOnlyList<JobRecord> result = await mContext.Jobs
                                                        .Find(filter)
                                                        .SortByDescending(j => j.CreatedAt)
                                                        .Limit(limit)
                                                        .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> ListRunningAsync(JobType? jobType = null,
                                                                  CancellationToken ct = default)
    {
        FilterDefinition<JobRecord> filter = jobType is null
            ? Builders<JobRecord>.Filter.Eq(j => j.Status, JobStatus.Running)
            : Builders<JobRecord>.Filter.And(
                Builders<JobRecord>.Filter.Eq(j => j.Status, JobStatus.Running),
                Builders<JobRecord>.Filter.Eq(j => j.JobType, jobType.Value)
            );

        IReadOnlyList<JobRecord> result = await mContext.Jobs
                                                        .Find(filter)
                                                        .SortByDescending(j => j.CreatedAt)
                                                        .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> ListActiveAsync(string libraryId,
                                                                 string? version = null,
                                                                 JobType? jobType = null,
                                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var clauses = new List<FilterDefinition<JobRecord>>
        {
            Builders<JobRecord>.Filter.Eq(j => j.LibraryId, libraryId),
            Builders<JobRecord>.Filter.In(j => j.Status, smNonTerminalStatuses)
        };
        if (!string.IsNullOrEmpty(version))
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.Version, version));
        if (jobType.HasValue)
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.JobType, jobType.Value));

        IReadOnlyList<JobRecord> result = await mContext.Jobs
                                                        .Find(Builders<JobRecord>.Filter.And(clauses))
                                                        .SortByDescending(j => j.CreatedAt)
                                                        .ToListAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<JobRecord?> GetActiveAsync(string libraryId,
                                                  string version,
                                                  JobType jobType,
                                                  CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        FilterDefinition<JobRecord> filter = Builders<JobRecord>.Filter.And(
            Builders<JobRecord>.Filter.Eq(j => j.LibraryId, libraryId),
            Builders<JobRecord>.Filter.Eq(j => j.Version, version),
            Builders<JobRecord>.Filter.Eq(j => j.JobType, jobType),
            Builders<JobRecord>.Filter.In(j => j.Status, smNonTerminalStatuses)
        );

        JobRecord? result = await mContext.Jobs
                                          .Find(filter)
                                          .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        DeleteResult result = await mContext.Jobs.DeleteOneAsync(j => j.Id == jobId, ct);
        return result.DeletedCount > 0;
    }

    /// <inheritdoc />
    public async Task<long> DeleteManyAsync(JobType? jobType,
                                             JobStatus? status,
                                             string? libraryId,
                                             string? version,
                                             DateTime? completedBefore,
                                             CancellationToken ct = default)
    {
        FilterDefinition<JobRecord>? filter = BuildDeleteFilter(jobType, status, libraryId, version, completedBefore);
        long result = 0;
        if (filter != null)
        {
            DeleteResult deletion = await mContext.Jobs.DeleteManyAsync(filter, ct);
            result = deletion.DeletedCount;
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<long> CountDeleteCandidatesAsync(JobType? jobType,
                                                        JobStatus? status,
                                                        string? libraryId,
                                                        string? version,
                                                        DateTime? completedBefore,
                                                        CancellationToken ct = default)
    {
        FilterDefinition<JobRecord>? filter = BuildDeleteFilter(jobType, status, libraryId, version, completedBefore);
        long result = 0;
        if (filter != null)
            result = await mContext.Jobs.CountDocumentsAsync(filter, cancellationToken: ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRecord>> ListDeleteCandidatesAsync(JobType? jobType,
                                                                           JobStatus? status,
                                                                           string? libraryId,
                                                                           string? version,
                                                                           DateTime? completedBefore,
                                                                           int limit,
                                                                           CancellationToken ct = default)
    {
        FilterDefinition<JobRecord>? filter = BuildDeleteFilter(jobType, status, libraryId, version, completedBefore);
        IReadOnlyList<JobRecord> result = [];
        if (filter != null)
        {
            result = await mContext.Jobs
                                   .Find(filter)
                                   .SortByDescending(j => j.CreatedAt)
                                   .Limit(limit > 0 ? limit : DefaultCandidateLimit)
                                   .ToListAsync(ct);
        }
        return result;
    }

    private static FilterDefinition<JobRecord>? BuildDeleteFilter(JobType? jobType,
                                                                   JobStatus? status,
                                                                   string? libraryId,
                                                                   string? version,
                                                                   DateTime? completedBefore)
    {
        var clauses = new List<FilterDefinition<JobRecord>>();
        if (jobType.HasValue)
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.JobType, jobType.Value));
        if (status.HasValue)
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.Status, status.Value));
        if (!string.IsNullOrEmpty(libraryId))
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.LibraryId, libraryId));
        if (!string.IsNullOrEmpty(version))
            clauses.Add(Builders<JobRecord>.Filter.Eq(j => j.Version, version));
        if (completedBefore.HasValue)
            clauses.Add(Builders<JobRecord>.Filter.Lt(j => j.CompletedAt, completedBefore.Value));

        // All-null filter is refused so a typo at the call site doesn't
        // truncate the whole jobs collection. Callers that really want
        // "every job" must compose that explicitly via DeleteAsync per id.
        FilterDefinition<JobRecord>? result = clauses.Count == 0
            ? null
            : Builders<JobRecord>.Filter.And(clauses);
        return result;
    }

    private static readonly JobStatus[] smNonTerminalStatuses =
    [
        JobStatus.Queued,
        JobStatus.Running
    ];

    private const int DefaultCandidateLimit = 20;
}
