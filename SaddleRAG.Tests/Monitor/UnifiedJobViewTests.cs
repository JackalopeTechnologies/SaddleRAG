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
                                         LibraryId           = "foo",
                                         Version             = "1.0",
                                         RootUrl             = "https://x",
                                         LibraryHint         = "foo docs",
                                         AllowedUrlPatterns  = []
                                     },
                           Status         = ScrapeJobStatus.Completed,
                           CreatedAt      = DateTime.UtcNow,
                           PagesCompleted = 42,
                           ErrorCount     = 0
                       }
                  );

        var view = new UnifiedJobView(scrape, new FakeBackgroundJobRepository(), new FakeRescrubJobRepository());
        var rows = await view.ListAsync(statusFilter: null, typeFilter: null, libraryFilter: null, limit: 10,
                                        ct: TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal("s1", row.JobId);
        Assert.Equal(JobType.Scrape, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal("1.0", row.Version);
        Assert.Equal(42, row.ItemsProcessed);
        Assert.Equal("pages", row.ItemsLabel);
    }

    [Fact]
    public async Task DryRunBackgroundJobProjectsToDryRunRow()
    {
        var bg = new FakeBackgroundJobRepository();
        bg.Add(new BackgroundJobRecord
                   {
                       Id             = "b1",
                       JobType        = BackgroundJobTypes.DryRunScrape,
                       LibraryId      = "foo",
                       Version        = "1.0",
                       InputJson      = "{}",
                       Status         = ScrapeJobStatus.Completed,
                       CreatedAt      = DateTime.UtcNow,
                       ItemsProcessed = 50,
                       ItemsTotal     = 50,
                       ItemsLabel     = "pages"
                   }
              );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), bg, new FakeRescrubJobRepository());
        var rows = await view.ListAsync(null, null, null, 10, ct: TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(JobType.DryRunScrape, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal(50, row.ItemsProcessed);
        Assert.Equal("pages", row.ItemsLabel);
    }

    [Fact]
    public async Task RescrubJobProjectsToRescrubRow()
    {
        var rescrub = new FakeRescrubJobRepository();
        rescrub.Add(new RescrubJobRecord
                        {
                            Id              = "r1",
                            LibraryId       = "foo",
                            Version         = "1.0",
                            Options         = new RescrubOptions(),
                            Status          = ScrapeJobStatus.Running,
                            CreatedAt       = DateTime.UtcNow,
                            ChunksProcessed = 100,
                            ChunksTotal     = 200
                        }
                   );

        var view = new UnifiedJobView(new FakeScrapeJobRepository(), new FakeBackgroundJobRepository(), rescrub);
        var rows = await view.ListAsync(null, null, null, 10, ct: TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(JobType.Rescrub, row.Type);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal(100, row.ItemsProcessed);
        Assert.Equal(200, row.ItemsTotal);
        Assert.Equal("chunks", row.ItemsLabel);
    }
}
