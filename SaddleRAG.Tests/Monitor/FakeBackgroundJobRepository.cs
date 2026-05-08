// FakeBackgroundJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeBackgroundJobRepository : IBackgroundJobRepository
{
    private readonly List<BackgroundJobRecord> mJobs = new();

    public void Add(BackgroundJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        mJobs.Add(job);
    }

    public Task UpsertAsync(BackgroundJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = mJobs.FindIndex(j => j.Id == job.Id);
        if (existing >= 0)
            mJobs[existing] = job;
        else
            mJobs.Add(job);
        return Task.CompletedTask;
    }

    public Task<BackgroundJobRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(mJobs.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<BackgroundJobRecord>> ListRecentAsync(string? jobType = null,
                                                                    int limit = 20,
                                                                    CancellationToken ct = default)
    {
        IReadOnlyList<BackgroundJobRecord> result = mJobs
                                                   .Where(j => jobType is null || j.JobType == jobType)
                                                   .OrderByDescending(j => j.CreatedAt)
                                                   .Take(limit)
                                                   .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeBackgroundJobRepository: DeleteAsync not supported in this test");

    public Task<long> DeleteManyAsync(ScrapeJobStatus? status,
                                      string? libraryId,
                                      string? version,
                                      CancellationToken ct = default) =>
        throw new NotSupportedException("FakeBackgroundJobRepository: DeleteManyAsync not supported in this test");

    public Task<long> CountDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                 string? libraryId,
                                                 string? version,
                                                 CancellationToken ct = default) =>
        throw new
            NotSupportedException("FakeBackgroundJobRepository: CountDeleteCandidatesAsync not supported in this test");

    public Task<IReadOnlyList<BackgroundJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                                              string? libraryId,
                                                                              string? version,
                                                                              int limit,
                                                                              CancellationToken ct = default) =>
        throw new
            NotSupportedException("FakeBackgroundJobRepository: ListDeleteCandidatesAsync not supported in this test");
}
