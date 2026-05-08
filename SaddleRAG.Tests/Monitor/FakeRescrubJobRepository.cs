// FakeRescrubJobRepository.cs
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

internal sealed class FakeRescrubJobRepository : IRescrubJobRepository
{
    private readonly List<RescrubJobRecord> mJobs = new();

    public void Add(RescrubJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        mJobs.Add(job);
    }

    public Task UpsertAsync(RescrubJobRecord job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var existing = mJobs.FindIndex(j => j.Id == job.Id);
        if (existing >= 0)
            mJobs[existing] = job;
        else
            mJobs.Add(job);
        return Task.CompletedTask;
    }

    public Task<RescrubJobRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(mJobs.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<RescrubJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        IReadOnlyList<RescrubJobRecord> result = mJobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeRescrubJobRepository: DeleteAsync not supported in this test");

    public Task<long> DeleteManyAsync(ScrapeJobStatus? status,
                                      string? libraryId,
                                      string? version,
                                      CancellationToken ct = default) =>
        throw new NotSupportedException("FakeRescrubJobRepository: DeleteManyAsync not supported in this test");

    public Task<long> CountDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                 string? libraryId,
                                                 string? version,
                                                 CancellationToken ct = default) =>
        throw new NotSupportedException("FakeRescrubJobRepository: CountDeleteCandidatesAsync not supported in this test"
                                       );

    public Task<IReadOnlyList<RescrubJobRecord>> ListDeleteCandidatesAsync(ScrapeJobStatus? status,
                                                                           string? libraryId,
                                                                           string? version,
                                                                           int limit,
                                                                           CancellationToken ct = default) =>
        throw new NotSupportedException("FakeRescrubJobRepository: ListDeleteCandidatesAsync not supported in this test"
                                       );
}
