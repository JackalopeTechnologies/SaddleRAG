// UnifiedJobViewTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
        var scrape = new FakeScrapeJobRepository();
        scrape.Add(new ScrapeJobRecord
                       {
                           Id = "s1",
                           Job = new ScrapeJob
                                     {
                                         LibraryId = "foo",
                                         Version = "1.0",
                                         RootUrl = "https://x",
                                         LibraryHint = "foo docs",
                                         AllowedUrlPatterns = []
                                     },
                           Status = ScrapeJobStatus.Completed,
                           CreatedAt = DateTime.UtcNow,
                           PagesCompleted = 42,
                           ErrorCount = 0
                       }
                  );

        var view = new UnifiedJobView(scrape, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("s1", row.JobId);
        Assert.Equal(JobType.Scrape, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal("1.0", row.Version);
        Assert.Equal(expected: 42, row.ItemsProcessed);
        Assert.Equal("pages", row.ItemsLabel);
    }

    [Fact]
    public async Task DryRunBackgroundJobProjectsToDryRunRow()
    {
        var bg = new FakeBackgroundJobRepository();
        bg.Add(new BackgroundJobRecord
                   {
                       Id = "b1",
                       JobType = BackgroundJobTypes.DryRunScrape,
                       LibraryId = "foo",
                       Version = "1.0",
                       InputJson = "{}",
                       Status = ScrapeJobStatus.Completed,
                       CreatedAt = DateTime.UtcNow,
                       ItemsProcessed = 50,
                       ItemsTotal = 50,
                       ItemsLabel = "pages"
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(JobType.DryRunScrape, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal(expected: 50, row.ItemsProcessed);
        Assert.Equal("pages", row.ItemsLabel);
    }

    [Fact]
    public async Task RescrubJobProjectsToRescrubRow()
    {
        var rescrub = new FakeRescrubJobRepository();
        rescrub.Add(new RescrubJobRecord
                        {
                            Id = "r1",
                            LibraryId = "foo",
                            Version = "1.0",
                            Options = new RescrubOptions(),
                            Status = ScrapeJobStatus.Running,
                            CreatedAt = DateTime.UtcNow,
                            ChunksProcessed = 100,
                            ChunksTotal = 200
                        }
                   );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), new FakeBackgroundJobRepository(), rescrub);
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(JobType.Rescrub, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal(expected: 100, row.ItemsProcessed);
        Assert.Equal(expected: 200, row.ItemsTotal);
        Assert.Equal("chunks", row.ItemsLabel);
    }

    [Fact]
    public async Task ListSortsByCreatedAtDescAndAppliesLimit()
    {
        var scrape = new FakeScrapeJobRepository();
        var bg = new FakeBackgroundJobRepository();
        var rescrub = new FakeRescrubJobRepository();
        var now = DateTime.UtcNow;

        scrape.Add(MakeScrape("s_old", now.AddMinutes(value: -30)));
        bg.Add(MakeBackground("b_mid", BackgroundJobTypes.Rechunk, now.AddMinutes(value: -20)));
        rescrub.Add(MakeRescrub("r_new", now.AddMinutes(value: -10)));

        var view = new UnifiedJobView(scrape, bg, rescrub);
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
        var scrape = new FakeScrapeJobRepository();
        scrape.Add(MakeScrape("a", DateTime.UtcNow));
        scrape.Add(MakeScrape("b", DateTime.UtcNow.AddSeconds(value: -1), ScrapeJobStatus.Failed));

        var view = new UnifiedJobView(scrape, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository());
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
        var scrape = new FakeScrapeJobRepository();
        scrape.Add(MakeScrape("s", DateTime.UtcNow));
        var bg = new FakeBackgroundJobRepository();
        bg.Add(MakeBackground("b", BackgroundJobTypes.Rechunk, DateTime.UtcNow));

        var view = new UnifiedJobView(scrape, bg, new FakeRescrubJobRepository());
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
        var bg = new FakeBackgroundJobRepository();
        bg.Add(MakeBackground("b1", BackgroundJobTypes.Rechunk, DateTime.UtcNow));
        bg.Add(new BackgroundJobRecord
                   {
                       Id = "b2",
                       JobType = BackgroundJobTypes.RenameLibrary,
                       LibraryId = null,
                       Version = null,
                       InputJson = "{}",
                       Status = ScrapeJobStatus.Completed,
                       CreatedAt = DateTime.UtcNow.AddSeconds(value: -1)
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        "x",
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal("b1", row.JobId);
    }

    [Fact]
    public async Task MalformedInputJsonDoesNotCrashProjection()
    {
        var bg = new FakeBackgroundJobRepository();
        bg.Add(new BackgroundJobRecord
                   {
                       Id = "b1",
                       JobType = BackgroundJobTypes.RenameLibrary,
                       InputJson = "{not-json",
                       Status = ScrapeJobStatus.Completed,
                       CreatedAt = DateTime.UtcNow
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
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
    [InlineData(BackgroundJobTypes.CleanupAuditLog, JobType.CleanupAuditLog)]
    [InlineData(BackgroundJobTypes.CleanupJobs, JobType.CleanupJobs)]
    [InlineData(BackgroundJobTypes.CleanupOrphans, JobType.CleanupOrphans)]
    public async Task CleanupBackgroundJobTypesMapToTheirJobType(string backgroundType, JobType expected)
    {
        var bg = new FakeBackgroundJobRepository();
        bg.Add(new BackgroundJobRecord
                   {
                       Id = "b1",
                       JobType = backgroundType,
                       LibraryId = "x",
                       Version = "1",
                       InputJson = "{}",
                       Status = ScrapeJobStatus.Completed,
                       CreatedAt = DateTime.UtcNow
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null,
                                        typeFilter: null,
                                        libraryFilter: null,
                                        limit: 10,
                                        TestContext.Current.CancellationToken
                                       );

        var row = Assert.Single(rows);
        Assert.Equal(expected, row.Type);
    }

    [Fact]
    public async Task UnknownBackgroundJobTypeMapsToUnknown()
    {
        var bg = new FakeBackgroundJobRepository();
        bg.Add(new BackgroundJobRecord
                   {
                       Id = "b_unknown",
                       JobType = "future_unknown_type",
                       LibraryId = "x",
                       Version = "1",
                       InputJson = "{}",
                       Status = ScrapeJobStatus.Completed,
                       CreatedAt = DateTime.UtcNow
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
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

    private static ScrapeJobRecord MakeScrape(string id,
                                              DateTime createdAt,
                                              ScrapeJobStatus status = ScrapeJobStatus.Completed) =>
        new ScrapeJobRecord
            {
                Id = id,
                Job = new ScrapeJob
                          {
                              LibraryId = "x",
                              Version = "1",
                              RootUrl = "https://x",
                              LibraryHint = "test",
                              AllowedUrlPatterns = []
                          },
                Status = status,
                CreatedAt = createdAt
            };

    private static BackgroundJobRecord MakeBackground(string id, string jobType, DateTime createdAt) =>
        new BackgroundJobRecord
            {
                Id = id,
                JobType = jobType,
                LibraryId = "x",
                Version = "1",
                InputJson = "{}",
                Status = ScrapeJobStatus.Completed,
                CreatedAt = createdAt
            };

    private static RescrubJobRecord MakeRescrub(string id, DateTime createdAt) =>
        new RescrubJobRecord
            {
                Id = id,
                LibraryId = "x",
                Version = "1",
                Status = ScrapeJobStatus.Completed,
                CreatedAt = createdAt,
                Options = new RescrubOptions()
            };

    #endregion
}
