// UnifiedJobViewHelpersTests.cs
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

public sealed class UnifiedJobViewHelpersTests
{
    #region ApplyFilters

    [Fact]
    public void ApplyFiltersWithNoFiltersOrdersByCreatedAtDescThenJobIdAsc()
    {
        var t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
                       {
                           MakeRow("b", t0.AddMinutes(2), ScrapeJobStatus.Running, "lib"),
                           MakeRow("a", t0.AddMinutes(2), ScrapeJobStatus.Running, "lib"),
                           MakeRow("c", t0, ScrapeJobStatus.Completed, "lib")
                       };

        var result = UnifiedJobView.ApplyFilters(rows, statusFilter: null, libraryFilter: null, limit: 10);

        Assert.Collection(result,
                          r => Assert.Equal("a", r.JobId),
                          r => Assert.Equal("b", r.JobId),
                          r => Assert.Equal("c", r.JobId)
                         );
    }

    [Fact]
    public void ApplyFiltersStatusFilterKeepsOnlyMatchingRows()
    {
        var rows = new[]
                       {
                           MakeRow("a", DateTime.UtcNow, ScrapeJobStatus.Running, "lib"),
                           MakeRow("b", DateTime.UtcNow, ScrapeJobStatus.Completed, "lib"),
                           MakeRow("c", DateTime.UtcNow, ScrapeJobStatus.Failed, "lib")
                       };

        var result = UnifiedJobView.ApplyFilters(rows,
                                                  statusFilter: ScrapeJobStatus.Completed,
                                                  libraryFilter: null,
                                                  limit: 10
                                                 );

        Assert.Single(result, r => r.JobId == "b");
    }

    [Fact]
    public void ApplyFiltersLibraryFilterIsCaseInsensitiveSubstring()
    {
        var rows = new[]
                       {
                           MakeRow("a", DateTime.UtcNow, ScrapeJobStatus.Running, "aerotech-aeroscript"),
                           MakeRow("b", DateTime.UtcNow, ScrapeJobStatus.Running, "mongodb.driver"),
                           MakeRow("c", DateTime.UtcNow, ScrapeJobStatus.Running, "AeroTech-foo")
                       };

        var result = UnifiedJobView.ApplyFilters(rows,
                                                  statusFilter: null,
                                                  libraryFilter: "AEROTECH",
                                                  limit: 10
                                                 );

        Assert.Equal(expected: 2, result.Count);
        Assert.Contains(result, r => r.JobId == "a");
        Assert.Contains(result, r => r.JobId == "c");
    }

    [Fact]
    public void ApplyFiltersTruncatesToLimit()
    {
        var rows = Enumerable.Range(start: 0, count: 10)
                             .Select(i => MakeRow($"j{i}",
                                                  DateTime.UtcNow.AddMinutes(i),
                                                  ScrapeJobStatus.Running,
                                                  "lib"
                                                 )
                                    )
                             .ToList();

        var result = UnifiedJobView.ApplyFilters(rows, statusFilter: null, libraryFilter: null, limit: 3);

        Assert.Equal(expected: 3, result.Count);
    }

    [Fact]
    public void ApplyFiltersIgnoresLibraryFilterWhenLibraryIdNull()
    {
        var rows = new[]
                       {
                           MakeRow("a", DateTime.UtcNow, ScrapeJobStatus.Running, libraryId: null),
                           MakeRow("b", DateTime.UtcNow, ScrapeJobStatus.Running, "foo")
                       };

        var result = UnifiedJobView.ApplyFilters(rows,
                                                  statusFilter: null,
                                                  libraryFilter: "foo",
                                                  limit: 10
                                                 );

        Assert.Single(result, r => r.JobId == "b");
    }

    #endregion

    #region Project

    [Fact]
    public void ProjectMapsAllCoreFields()
    {
        var record = new JobRecord
                         {
                             Id = "job-1",
                             JobType = JobType.Scrape,
                             Status = JobStatus.Running,
                             CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                             StartedAt = new DateTime(2026, 5, 1, 0, 1, 0, DateTimeKind.Utc),
                             LibraryId = "foo",
                             Version = "1.0",
                             ItemsProcessed = 5,
                             ItemsTotal = 10,
                             ItemsLabel = "pages",
                             ErrorCount = 0
                         };

        var row = UnifiedJobView.Project(record);

        Assert.Equal("job-1", row.JobId);
        Assert.Equal(JobType.Scrape, row.Type);
        Assert.Equal(ScrapeJobStatus.Running, row.Status);
        Assert.Equal("foo", row.LibraryId);
        Assert.Equal("1.0", row.Version);
        Assert.Equal(expected: 5, row.ItemsProcessed);
        Assert.Equal(expected: 10, row.ItemsTotal);
        Assert.Equal("pages", row.ItemsLabel);
        Assert.Null(row.RenameToId);
        Assert.Null(row.ScanPath);
    }

    [Fact]
    public void ProjectExtractsNewIdHintForRenameLibraryJobs()
    {
        var record = new JobRecord
                         {
                             Id = "job-2",
                             JobType = JobType.RenameLibrary,
                             Status = JobStatus.Queued,
                             CreatedAt = DateTime.UtcNow,
                             LibraryId = "old-id",
                             InputJson = "{\"newId\":\"new-id\"}"
                         };

        var row = UnifiedJobView.Project(record);

        Assert.Equal("new-id", row.RenameToId);
        Assert.Null(row.ScanPath);
    }

    [Fact]
    public void ProjectExtractsPathHintForIndexProjectDependenciesJobs()
    {
        var record = new JobRecord
                         {
                             Id = "job-3",
                             JobType = JobType.IndexProjectDependencies,
                             Status = JobStatus.Queued,
                             CreatedAt = DateTime.UtcNow,
                             InputJson = "{\"path\":\"/some/repo\"}"
                         };

        var row = UnifiedJobView.Project(record);

        Assert.Equal("/some/repo", row.ScanPath);
        Assert.Null(row.RenameToId);
    }

    [Fact]
    public void ProjectLeavesHintsNullForJobTypesWithoutHints()
    {
        var record = new JobRecord
                         {
                             Id = "job-4",
                             JobType = JobType.Scrape,
                             Status = JobStatus.Queued,
                             CreatedAt = DateTime.UtcNow,
                             InputJson = "{\"newId\":\"ignored\",\"path\":\"/ignored\"}"
                         };

        var row = UnifiedJobView.Project(record);

        Assert.Null(row.RenameToId);
        Assert.Null(row.ScanPath);
    }

    [Fact]
    public void ProjectTreatsMalformedInputJsonAsNoHint()
    {
        var record = new JobRecord
                         {
                             Id = "job-5",
                             JobType = JobType.RenameLibrary,
                             Status = JobStatus.Queued,
                             CreatedAt = DateTime.UtcNow,
                             InputJson = "{this isn't json"
                         };

        var row = UnifiedJobView.Project(record);

        Assert.Null(row.RenameToId);
    }

    #endregion

    private static JobRow MakeRow(string jobId,
                                  DateTime createdAt,
                                  ScrapeJobStatus status,
                                  string? libraryId) =>
        new JobRow
            {
                JobId = jobId,
                Type = JobType.Scrape,
                Status = status,
                CreatedAt = createdAt,
                LibraryId = libraryId,
                Version = "1.0",
                ItemsProcessed = 0,
                ItemsTotal = 0,
                ItemsLabel = "items",
                ErrorCount = 0
            };
}
