// ScrapeJobRepositoryDeleteTests.cs
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
///     Integration coverage for the new manual-cleanup delete methods on
///     <see cref="ScrapeJobRepository" />. Runs against a live local
///     MongoDB instance under the SaddleRAG_test_jobs database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScrapeJobRepositoryDeleteTests
{
    public ScrapeJobRepositoryDeleteTests()
    {
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = TestDatabaseName
                                          }
                                     );
        mContext = new SaddleRagDbContext(settings);
        mRepo = new ScrapeJobRepository(mContext);
    }

    private readonly SaddleRagDbContext mContext;
    private readonly ScrapeJobRepository mRepo;

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
        var matchA = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0");
        var matchB = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0");
        var differentVersion = MakeJob(ScrapeJobStatus.Cancelled, lib, "2.0");
        var differentStatus = MakeJob(ScrapeJobStatus.Completed, lib, "1.0");
        await mRepo.UpsertAsync(matchA, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(matchB, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(differentVersion, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(differentStatus, TestContext.Current.CancellationToken);

        long deleted = await mRepo.DeleteManyAsync(ScrapeJobStatus.Cancelled,
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
    public async Task CountDeleteCandidatesMatchesDeleteCount()
    {
        var lib = $"lib-{Guid.NewGuid():N}";
        var jobA = MakeJob(ScrapeJobStatus.Failed, lib, "1.0");
        var jobB = MakeJob(ScrapeJobStatus.Failed, lib, "1.0");
        await mRepo.UpsertAsync(jobA, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(jobB, TestContext.Current.CancellationToken);

        long count = await mRepo.CountDeleteCandidatesAsync(ScrapeJobStatus.Failed,
                                                            lib,
                                                            "1.0",
                                                            TestContext.Current.CancellationToken
                                                           );
        long deleted = await mRepo.DeleteManyAsync(ScrapeJobStatus.Failed,
                                                   lib,
                                                   "1.0",
                                                   TestContext.Current.CancellationToken
                                                  );

        Assert.Equal(expected: 2L, count);
        Assert.Equal(count, deleted);
    }

    [Fact]
    public async Task ListDeleteCandidatesReturnsMatchingRowsMostRecentFirst()
    {
        var lib = $"lib-{Guid.NewGuid():N}";
        var older = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0", DateTime.UtcNow.AddHours(OlderHoursOffset));
        var newer = MakeJob(ScrapeJobStatus.Cancelled, lib, "1.0", DateTime.UtcNow);
        await mRepo.UpsertAsync(older, TestContext.Current.CancellationToken);
        await mRepo.UpsertAsync(newer, TestContext.Current.CancellationToken);

        var candidates = await mRepo.ListDeleteCandidatesAsync(ScrapeJobStatus.Cancelled,
                                                               lib,
                                                               "1.0",
                                                               limit: 10,
                                                               TestContext.Current.CancellationToken
                                                              );

        Assert.Equal(expected: 2, candidates.Count);
        Assert.Equal(newer.Id, candidates[index: 0].Id);
        Assert.Equal(older.Id, candidates[index: 1].Id);
    }

    private static ScrapeJobRecord MakeJob(ScrapeJobStatus status, string libraryId, string version) =>
        MakeJob(status, libraryId, version, DateTime.UtcNow);

    private static ScrapeJobRecord MakeJob(ScrapeJobStatus status,
                                           string libraryId,
                                           string version,
                                           DateTime createdAt) =>
        new ScrapeJobRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Status = status,
                CreatedAt = createdAt,
                Job = new ScrapeJob
                          {
                              LibraryId = libraryId,
                              Version = version,
                              RootUrl = ExampleRootUrl,
                              LibraryHint = libraryId,
                              AllowedUrlPatterns = new[] { ExampleRootUrl }
                          }
            };

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string TestDatabaseName = "SaddleRAG_test_jobs";
    private const string ExampleRootUrl = "https://example.com";
    private const int OlderHoursOffset = -2;
}
