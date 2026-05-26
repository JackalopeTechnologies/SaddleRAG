// UnifiedJobViewTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class UnifiedJobViewTests
{
    [Fact]
    public async Task ScrapeJobProjectsToScrapeRow()
    {
        var fake = new FakeJobRepository();
        fake.Add(new JobRecord
                     {
                         Id = "s1",
                         JobType = JobType.Scrape,
                         LibraryId = LibraryFoo,
                         Version = VersionOne,
                         InputJson = "{}",
                         Status = JobStatus.Completed,
                         CreatedAt = DateTime.UtcNow,
                         ItemsProcessed = 42,
                         ItemsTotal = 0,
                         ItemsLabel = PagesLabel,
                         ScrapeProgress = new ScrapeProgress { PagesCompleted = 42 }
                     }
                );

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("s1", row.JobId);
        Assert.Equal(JobType.Scrape, row.Type);
        Assert.Equal(LibraryFoo, row.LibraryId);
        Assert.Equal(VersionOne, row.Version);
        Assert.Equal(expected: 42, row.ItemsProcessed);
        Assert.Equal(PagesLabel, row.ItemsLabel);
    }

    [Fact]
    public async Task DryRunBackgroundJobProjectsToDryRunRow()
    {
        var fake = new FakeJobRepository();
        fake.Add(new JobRecord
                     {
                         Id = "b1",
                         JobType = JobType.DryRunScrape,
                         LibraryId = LibraryFoo,
                         Version = VersionOne,
                         InputJson = "{}",
                         Status = JobStatus.Completed,
                         CreatedAt = DateTime.UtcNow,
                         ItemsProcessed = 50,
                         ItemsTotal = 50,
                         ItemsLabel = PagesLabel
                     }
                );

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(JobType.DryRunScrape, row.Type);
        Assert.Equal(LibraryFoo, row.LibraryId);
        Assert.Equal(expected: 50, row.ItemsProcessed);
        Assert.Equal(PagesLabel, row.ItemsLabel);
    }

    [Fact]
    public async Task RescrubJobProjectsToRescrubRow()
    {
        var fake = new FakeJobRepository();
        fake.Add(new JobRecord
                     {
                         Id = "r1",
                         JobType = JobType.Rescrub,
                         LibraryId = LibraryFoo,
                         Version = VersionOne,
                         InputJson = "{}",
                         Status = JobStatus.Running,
                         CreatedAt = DateTime.UtcNow,
                         ItemsProcessed = 100,
                         ItemsTotal = 200,
                         ItemsLabel = ChunksLabel
                     }
                );

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(JobType.Rescrub, row.Type);
        Assert.Equal(LibraryFoo, row.LibraryId);
        Assert.Equal(expected: 100, row.ItemsProcessed);
        Assert.Equal(expected: 200, row.ItemsTotal);
        Assert.Equal(ChunksLabel, row.ItemsLabel);
    }

    [Fact]
    public async Task ListSortsByCreatedAtDescAndAppliesLimit()
    {
        var fake = new FakeJobRepository();
        var now = DateTime.UtcNow;
        fake.Add(MakeJob("s_old", JobType.Scrape, now.AddMinutes(value: -30)));
        fake.Add(MakeJob("b_mid", JobType.Rechunk, now.AddMinutes(value: -20)));
        fake.Add(MakeJob("r_new", JobType.Rescrub, now.AddMinutes(value: -10)));

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 2,
                                        TestContext.Current.CancellationToken
                                       );

        Assert.Equal(expected: 2, rows.Count);
        Assert.Equal("r_new", rows[index: 0].JobId);
        Assert.Equal("b_mid", rows[index: 1].JobId);
    }

    [Fact]
    public async Task ListFiltersByStatus()
    {
        var fake = new FakeJobRepository();
        fake.Add(MakeJob("a", JobType.Scrape, DateTime.UtcNow));
        fake.Add(MakeJob("b", JobType.Scrape, DateTime.UtcNow.AddSeconds(value: -1), JobStatus.Failed));

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(ScrapeJobStatus.Failed,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("b", row.JobId);
    }

    [Fact]
    public async Task ListFiltersByType()
    {
        var fake = new FakeJobRepository();
        fake.Add(MakeJob("s", JobType.Scrape, DateTime.UtcNow));
        fake.Add(MakeJob("b", JobType.Rechunk, DateTime.UtcNow));

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        JobType.Rechunk,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("b", row.JobId);
    }

    [Fact]
    public async Task LibraryFilterExcludesRowsWithNoLibrary()
    {
        var fake = new FakeJobRepository();
        fake.Add(MakeJob("b1", JobType.Rechunk, DateTime.UtcNow));
        fake.Add(new JobRecord
                     {
                         Id = "b2",
                         JobType = JobType.RenameLibrary,
                         LibraryId = null,
                         Version = null,
                         InputJson = "{}",
                         Status = JobStatus.Completed,
                         CreatedAt = DateTime.UtcNow.AddSeconds(value: -1)
                     }
                );

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        LibraryFilterMatch,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("b1", row.JobId);
    }

    [Fact]
    public async Task MalformedInputJsonDoesNotCrashProjection()
    {
        var fake = new FakeJobRepository();
        fake.Add(new JobRecord
                     {
                         Id = "b1",
                         JobType = JobType.RenameLibrary,
                         InputJson = "{not-json",
                         Status = JobStatus.Completed,
                         CreatedAt = DateTime.UtcNow
                     }
                );

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Null(row.RenameToId);
    }

    [Theory]
    [InlineData(JobType.CleanupAuditLog)]
    [InlineData(JobType.CleanupJobs)]
    [InlineData(JobType.CleanupOrphans)]
    public async Task CleanupJobTypesPassThrough(JobType cleanupType)
    {
        var fake = new FakeJobRepository();
        fake.Add(MakeJob("b1", cleanupType, DateTime.UtcNow));

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(cleanupType, row.Type);
    }

    [Fact]
    public async Task UnknownJobTypeProjectsAsUnknown()
    {
        var fake = new FakeJobRepository();
        fake.Add(MakeJob("b_unknown", JobType.Unknown, DateTime.UtcNow));

        var view = new UnifiedJobView(fake);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(JobType.Unknown, row.Type);
    }

    #region Helper methods

    private static JobRecord MakeJob(string id,
                                     JobType jobType,
                                     DateTime createdAt,
                                     JobStatus status = JobStatus.Completed) =>
        new JobRecord
            {
                Id = id,
                JobType = jobType,
                LibraryId = LibraryX,
                Version = VersionOne,
                InputJson = "{}",
                Status = status,
                CreatedAt = createdAt
            };

    private const string LibraryFoo = "foo";
    private const string LibraryX = "x";
    private const string VersionOne = "1";
    private const string PagesLabel = "pages";
    private const string ChunksLabel = "chunks";
    private const string LibraryFilterMatch = "x";

    #endregion
}
