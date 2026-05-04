// FakeScrapeJobRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Monitor;

internal sealed class FakeScrapeJobRepository : IScrapeJobRepository
{
    private readonly List<ScrapeJobRecord> mJobs = new();

    public void Add(ScrapeJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        mJobs.Add(job);
    }

    public Task<IReadOnlyList<ScrapeJobRecord>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        IReadOnlyList<ScrapeJobRecord> result = mJobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToList();
        return Task.FromResult(result);
    }

    public Task UpsertAsync(ScrapeJobRecord job, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeJobRepository: UpsertAsync not supported in this test");

    public Task<ScrapeJobRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(mJobs.FirstOrDefault(j => j.Id == id));

    public Task<IReadOnlyList<ScrapeJobRecord>> ListRunningJobsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeJobRepository: ListRunningJobsAsync not supported in this test");

    public Task<IReadOnlyList<ScrapeJobRecord>> ListActiveJobsAsync(string libraryId,
                                                                    string version,
                                                                    CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeJobRepository: ListActiveJobsAsync not supported in this test");

    public Task<ScrapeJobRecord?> GetActiveJobAsync(string libraryId,
                                                    string version,
                                                    CancellationToken ct = default) =>
        throw new NotSupportedException("FakeScrapeJobRepository: GetActiveJobAsync not supported in this test");
}
