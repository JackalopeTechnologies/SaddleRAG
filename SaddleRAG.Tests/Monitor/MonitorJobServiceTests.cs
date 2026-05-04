// MonitorJobServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorJobServiceTests
{
    [Fact]
    public async Task ListAsyncFiltersByStatus()
    {
        var repo = new FakeScrapeJobRepository();
        repo.Add(MakeJob("a", "alpha", "1", ScrapeJobStatus.Running, T(1)));
        repo.Add(MakeJob("b", "alpha", "1", ScrapeJobStatus.Completed, T(2)));
        repo.Add(MakeJob("c", "alpha", "1", ScrapeJobStatus.Failed, T(3)));
        var svc = new MonitorJobService(repo);

        var rows = await svc.ListAsync(status: ScrapeJobStatus.Failed,
                                       ct: TestContext.Current.CancellationToken);

        Assert.Single(rows);
        Assert.Equal("c", rows[0].JobId);
    }

    [Fact]
    public async Task ListAsyncFiltersByLibrarySubstringCaseInsensitive()
    {
        var repo = new FakeScrapeJobRepository();
        repo.Add(MakeJob("a", "MongoDB.Driver", "1", ScrapeJobStatus.Completed, T(1)));
        repo.Add(MakeJob("b", "AngleSharp",     "1", ScrapeJobStatus.Completed, T(2)));
        repo.Add(MakeJob("c", "mongodb.driver", "2", ScrapeJobStatus.Completed, T(3)));
        var svc = new MonitorJobService(repo);

        var rows = await svc.ListAsync(libraryIdFilter: "mongo",
                                       ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains("mongo", r.LibraryId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListAsyncRespectsLimit()
    {
        var repo = new FakeScrapeJobRepository();
        for (int i = 0; i < 5; i++)
        {
            repo.Add(MakeJob($"job-{i}", "alpha", "1", ScrapeJobStatus.Completed, T(i)));
        }

        var svc = new MonitorJobService(repo);

        var rows = await svc.ListAsync(limit: 3, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task ListAsyncProjectsAllJobFieldsCorrectly()
    {
        var started = T(10);
        var completed = T(15);
        var repo = new FakeScrapeJobRepository();
        repo.Add(new ScrapeJobRecord
                     {
                         Id = "j1",
                         Job = new ScrapeJob
                                   {
                                       LibraryId = "alpha",
                                       Version = "1",
                                       RootUrl = "https://x/",
                                       LibraryHint = string.Empty,
                                       AllowedUrlPatterns = Array.Empty<string>()
                                   },
                         Status = ScrapeJobStatus.Completed,
                         CreatedAt = T(9),
                         StartedAt = started,
                         CompletedAt = completed,
                         PagesCompleted = 42,
                         ErrorCount = 1,
                         ErrorMessage = "boom"
                     });
        var svc = new MonitorJobService(repo);

        var rows = await svc.ListAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal("j1", row.JobId);
        Assert.Equal("alpha", row.LibraryId);
        Assert.Equal("1", row.Version);
        Assert.Equal("Completed", row.Status);
        Assert.Equal(started, row.StartedAt);
        Assert.Equal(completed, row.CompletedAt);
        Assert.Equal(42, row.IndexedPageCount);
        Assert.Equal(1, row.ErrorCount);
        Assert.Equal("boom", row.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(5), row.Duration);
    }

    private static ScrapeJobRecord MakeJob(string id,
                                           string lib,
                                           string ver,
                                           ScrapeJobStatus status,
                                           DateTime createdAt) =>
        new ScrapeJobRecord
            {
                Id = id,
                Job = new ScrapeJob
                          {
                              LibraryId = lib,
                              Version = ver,
                              RootUrl = "https://x/",
                              LibraryHint = string.Empty,
                              AllowedUrlPatterns = Array.Empty<string>()
                          },
                Status = status,
                CreatedAt = createdAt
            };

    private static DateTime T(int sec) => new DateTime(2026, 1, 1, 0, 0, sec, DateTimeKind.Utc);
}
