// FakeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     In-memory <see cref="IJobRepository" /> for unit-testing the
///     unified job-collection consumers (<c>UnifiedJobView</c>,
///     <c>MonitorJobService</c>, …). Replaces the three
///     pre-unification fakes (<c>FakeScrapeJobRepository</c>,
///     <c>FakeBackgroundJobRepository</c>, <c>FakeRescrubJobRepository</c>).
///     Only methods exercised by tests are functional; the rest throw
///     <see cref="NotSupportedException" /> so misuse is loud.
/// </summary>
internal sealed class FakeJobRepository : IJobRepository
{
    public void Add(JobRecord record) => mRecords.Add(record);

    public Task UpsertAsync(JobRecord record, CancellationToken ct = default)
    {
        mRecords.RemoveAll(r => r.Id == record.Id);
        mRecords.Add(record);
        return Task.CompletedTask;
    }

    public Task<JobRecord?> GetAsync(string jobId, CancellationToken ct = default)
    {
        JobRecord? result = mRecords.FirstOrDefault(r => r.Id == jobId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<JobRecord>> ListRecentAsync(JobType? jobType = null,
                                                          int limit = 20,
                                                          CancellationToken ct = default)
    {
        IReadOnlyList<JobRecord> result = mRecords
            .Where(r => jobType is null || r.JobType == jobType)
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<JobRecord>> ListRunningAsync(JobType? jobType = null, CancellationToken ct = default)
    {
        IReadOnlyList<JobRecord> result = mRecords
            .Where(r => r.Status == JobStatus.Running)
            .Where(r => jobType is null || r.JobType == jobType)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<JobRecord>> ListActiveAsync(string libraryId,
                                                          string? version = null,
                                                          JobType? jobType = null,
                                                          CancellationToken ct = default)
    {
        IReadOnlyList<JobRecord> result = mRecords
            .Where(r => r.LibraryId == libraryId)
            .Where(r => string.IsNullOrEmpty(version) || r.Version == version)
            .Where(r => jobType is null || r.JobType == jobType)
            .Where(r => r.Status == JobStatus.Queued || r.Status == JobStatus.Running)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<JobRecord?> GetActiveAsync(string libraryId, string version, JobType jobType, CancellationToken ct = default)
    {
        JobRecord? result = mRecords.FirstOrDefault(r => r.LibraryId == libraryId
                                                          && r.Version == version
                                                          && r.JobType == jobType
                                                          && (r.Status == JobStatus.Queued ||
                                                              r.Status == JobStatus.Running));
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string jobId, CancellationToken ct = default)
    {
        int removed = mRecords.RemoveAll(r => r.Id == jobId);
        return Task.FromResult(removed > 0);
    }

    public Task<long> DeleteManyAsync(JobType? jobType, JobStatus? status, string? libraryId,
                                       string? version, DateTime? completedBefore, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<long> CountDeleteCandidatesAsync(JobType? jobType, JobStatus? status, string? libraryId,
                                                  string? version, DateTime? completedBefore, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<JobRecord>> ListDeleteCandidatesAsync(JobType? jobType, JobStatus? status,
                                                                     string? libraryId, string? version,
                                                                     DateTime? completedBefore, int limit,
                                                                     CancellationToken ct = default) =>
        throw new NotSupportedException();

    private readonly List<JobRecord> mRecords = new();
}
