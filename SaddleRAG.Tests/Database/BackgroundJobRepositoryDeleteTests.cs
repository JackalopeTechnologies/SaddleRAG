// BackgroundJobRepositoryDeleteTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Integration coverage for the cleanup-related delete methods on
///     <see cref="BackgroundJobRepository" />. Mirrors the scrape-job
///     repository tests so the safety guarantees (empty-filter no-op,
///     library isolation, missing-id false return) hold for the queued
///     background-job collection too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackgroundJobRepositoryDeleteTests
{
    public BackgroundJobRepositoryDeleteTests()
    {
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = TestDatabaseName
                                          }
                                     );
        mContext = new SaddleRagDbContext(settings);
        mRepo = new BackgroundJobRepository(mContext);
    }

    private readonly SaddleRagDbContext mContext;
    private readonly BackgroundJobRepository mRepo;

    [Fact]
    public async Task DeleteAsyncRemovesSingleRowAndReturnsTrue()
    {
        var job = MakeJob(ScrapeJobStatus.Failed, $"lib-{Guid.NewGuid():N}", "1.0");
        await mRepo.UpsertAsync(job, TestContext.Current.CancellationToken);

        bool removed = await mRepo.DeleteAsync(job.Id, TestContext.Current.CancellationToken);
        var refetched = await mRepo.GetAsync(job.Id, TestContext.Current.CancellationToken);

        Assert.True(removed);
        Assert.Null(refetched);
    }

    [Fact]
    public async Task DeleteAsyncReturnsFalseWhenIdMissing()
    {
        bool removed = await mRepo.DeleteAsync($"missing-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        Assert.False(removed);
    }

    [Fact]
    public async Task DeleteManyAsyncWithoutFiltersReturnsZero()
    {
        long deleted = await mRepo.DeleteManyAsync(status: null,
                                                   libraryId: null,
                                                   version: null,
                                                   TestContext.Current.CancellationToken
                                                  );
        Assert.Equal(expected: 0L, deleted);
    }

    [Fact]
    public async Task DeleteManyAsyncFiltersByStatusLibraryAndVersion()
    {
        var lib = $"lib-{Guid.NewGuid():N}";
        var matchA = MakeJob(ScrapeJobStatus.Failed, lib, "1.0");
        var matchB = MakeJob(ScrapeJobStatus.Failed, lib, "1.0");
        var differentVersion = MakeJob(ScrapeJobStatus.Failed, lib, "2.0");
        var differentStatus = MakeJob(ScrapeJobStatus.Completed, lib, "1.0");
        await mRepo.UpsertAsync(matchA, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(matchB, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(differentVersion, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(differentStatus, TestContext.Current.CancellationToken);

        long deleted = await mRepo.DeleteManyAsync(ScrapeJobStatus.Failed,
                                                   lib,
                                                   "1.0",
                                                   TestContext.Current.CancellationToken
                                                  );

        Assert.Equal(expected: 2L, deleted);
        Assert.Null(await mRepo.GetAsync(matchA.Id, TestContext.Current.CancellationToken));
        Assert.Null(await mRepo.GetAsync(matchB.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepo.GetAsync(differentVersion.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepo.GetAsync(differentStatus.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteManyAsyncIgnoresJobsFromDifferentLibrary()
    {
        var libA = $"lib-A-{Guid.NewGuid():N}";
        var libB = $"lib-B-{Guid.NewGuid():N}";
        var insideA = MakeJob(ScrapeJobStatus.Failed, libA, "1.0");
        var insideB = MakeJob(ScrapeJobStatus.Failed, libB, "1.0");
        await mRepo.UpsertAsync(insideA, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(insideB, TestContext.Current.CancellationToken);

        long deleted = await mRepo.DeleteManyAsync(ScrapeJobStatus.Failed,
                                                   libA,
                                                   "1.0",
                                                   TestContext.Current.CancellationToken
                                                  );

        Assert.Equal(expected: 1L, deleted);
        Assert.Null(await mRepo.GetAsync(insideA.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await mRepo.GetAsync(insideB.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CountDeleteCandidatesMatchesDeleteCount()
    {
        var lib = $"lib-{Guid.NewGuid():N}";
        var jobA = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0");
        var jobB = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0");
        await mRepo.UpsertAsync(jobA, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(jobB, TestContext.Current.CancellationToken);

        long count = await mRepo.CountDeleteCandidatesAsync(ScrapeJobStatus.Cancelled,
                                                            lib,
                                                            "1.0",
                                                            TestContext.Current.CancellationToken
                                                           );
        long deleted = await mRepo.DeleteManyAsync(ScrapeJobStatus.Cancelled,
                                                   lib,
                                                   "1.0",
                                                   TestContext.Current.CancellationToken
                                                  );

        Assert.Equal(expected: 2L, count);
        Assert.Equal(count, deleted);
    }

    [Fact]
    public async Task CountDeleteCandidatesReturnsZeroWithoutFilters()
    {
        long count = await mRepo.CountDeleteCandidatesAsync(status: null,
                                                            libraryId: null,
                                                            version: null,
                                                            TestContext.Current.CancellationToken
                                                           );
        Assert.Equal(expected: 0L, count);
    }

    private static BackgroundJobRecord MakeJob(ScrapeJobStatus status, string libraryId, string version) =>
        new BackgroundJobRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                JobType = BackgroundJobTypes.Rechunk,
                LibraryId = libraryId,
                Version = version,
                InputJson = "{}",
                Status = status,
                CreatedAt = DateTime.UtcNow
            };

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string TestDatabaseName = "SaddleRAG_test_jobs";
}
