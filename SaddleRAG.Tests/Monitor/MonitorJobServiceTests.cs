// MonitorJobServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorJobServiceTests
{
    [Fact]
    public async Task ListAsyncFiltersByStatus()
    {
        var repo = new FakeJobRepository();
        repo.Add(MakeJob("a", LibraryAlpha, VersionOne, JobStatus.Running,   T(sec: 1)));
        repo.Add(MakeJob("b", LibraryAlpha, VersionOne, JobStatus.Completed, T(sec: 2)));
        repo.Add(MakeJob("c", LibraryAlpha, VersionOne, JobStatus.Failed,    T(sec: 3)));
        var svc = new MonitorJobService(new UnifiedJobView(repo));

        var rows = await svc.ListAsync(ScrapeJobStatus.Failed,
                                       ct: TestContext.Current.CancellationToken
                                      );

        Assert.Single(rows);
        Assert.Equal("c", rows[index: 0].JobId);
    }

    [Fact]
    public async Task ListAsyncFiltersByLibrarySubstringCaseInsensitive()
    {
        var repo = new FakeJobRepository();
        repo.Add(MakeJob("a", "MongoDB.Driver", VersionOne, JobStatus.Completed, T(sec: 1)));
        repo.Add(MakeJob("b", "AngleSharp",     VersionOne, JobStatus.Completed, T(sec: 2)));
        repo.Add(MakeJob("c", "mongodb.driver", VersionTwo, JobStatus.Completed, T(sec: 3)));
        var svc = new MonitorJobService(new UnifiedJobView(repo));

        var rows = await svc.ListAsync(libraryIdFilter: "mongo",
                                       ct: TestContext.Current.CancellationToken
                                      );

        Assert.Equal(expected: 2, rows.Count);
        Assert.All(rows, r => Assert.Contains("mongo", r.LibraryId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListAsyncRespectsLimit()
    {
        var repo = new FakeJobRepository();
        foreach(int i in Enumerable.Range(start: 0, count: 5))
            repo.Add(MakeJob($"job-{i}", LibraryAlpha, VersionOne, JobStatus.Completed, T(i)));

        var svc = new MonitorJobService(new UnifiedJobView(repo));

        var rows = await svc.ListAsync(limit: 3, ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected: 3, rows.Count);
    }

    [Fact]
    public async Task ListAsyncProjectsAllJobFieldsCorrectly()
    {
        DateTime started   = T(sec: 10);
        DateTime completed = T(sec: 15);
        var repo = new FakeJobRepository();
        repo.Add(new JobRecord
                     {
                         Id             = "j1",
                         JobType        = JobType.Scrape,
                         LibraryId      = LibraryAlpha,
                         Version        = VersionOne,
                         InputJson      = "{}",
                         Status         = JobStatus.Completed,
                         CreatedAt      = T(sec: 9),
                         StartedAt      = started,
                         CompletedAt    = completed,
                         ItemsProcessed = 42,
                         ItemsLabel     = "pages",
                         ErrorCount     = 1,
                         ErrorMessage   = "boom"
                     }
                );
        var svc = new MonitorJobService(new UnifiedJobView(repo));

        var rows = await svc.ListAsync(ct: TestContext.Current.CancellationToken);

        Assert.Single(rows);
        var row = rows[index: 0];
        Assert.Equal("j1", row.JobId);
        Assert.Equal(LibraryAlpha, row.LibraryId);
        Assert.Equal(VersionOne, row.Version);
        Assert.Equal("Completed", row.Status);
        Assert.Equal(started,   row.StartedAt);
        Assert.Equal(completed, row.CompletedAt);
        Assert.Equal(expected: 42, row.ItemsProcessed);
        Assert.Equal(expected: 1,  row.ErrorCount);
        Assert.Equal("boom", row.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 5), row.Duration);
    }

    #region Helper methods

    private static JobRecord MakeJob(string id,
                                     string lib,
                                     string ver,
                                     JobStatus status,
                                     DateTime createdAt) =>
        new JobRecord
            {
                Id        = id,
                JobType   = JobType.Scrape,
                LibraryId = lib,
                Version   = ver,
                InputJson = "{}",
                Status    = status,
                CreatedAt = createdAt
            };

    private static DateTime T(int sec) => new DateTime(year: 2026,
                                                       month: 1,
                                                       day: 1,
                                                       hour: 0,
                                                       minute: 0,
                                                       sec,
                                                       DateTimeKind.Utc
                                                      );

    private const string LibraryAlpha = "alpha";
    private const string VersionOne   = "1";
    private const string VersionTwo   = "2";

    #endregion
}
