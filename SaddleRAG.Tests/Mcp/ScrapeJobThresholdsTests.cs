// ScrapeJobThresholdsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ScrapeJobThresholdsTests
{
    [Fact]
    public void IsStaleRunningTrueWhenLastProgressOlderThanCutoff()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(hours: 4);
        var job = MakeJob(JobStatus.Running,
                          now - TimeSpan.FromDays(days: 1),
                          now - TimeSpan.FromDays(days: 1)
                         );

        Assert.True(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFalseWhenLastProgressRecentEnough()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(hours: 4);
        var job = MakeJob(JobStatus.Running,
                          now - TimeSpan.FromHours(hours: 8),
                          now - TimeSpan.FromMinutes(minutes: 30)
                         );

        Assert.False(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFallsBackToCreatedAtWhenLastProgressNull()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(hours: 4);
        var job = MakeJob(JobStatus.Running,
                          now - TimeSpan.FromDays(days: 2),
                          lastProgressAt: null
                         );

        Assert.True(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    [Fact]
    public void IsStaleRunningFalseForNonRunningStatus()
    {
        var now = DateTime.UtcNow;
        var staleCutoff = now - TimeSpan.FromHours(hours: 4);
        var job = MakeJob(JobStatus.Cancelled,
                          now - TimeSpan.FromDays(days: 7),
                          now - TimeSpan.FromDays(days: 7)
                         );

        Assert.False(ScrapeJobThresholds.IsStaleRunning(job, staleCutoff));
    }

    private static JobRecord MakeJob(JobStatus status,
                                     DateTime createdAt,
                                     DateTime? lastProgressAt) =>
        new JobRecord
            {
                Id = "job",
                JobType = JobType.Scrape,
                LibraryId = "foo",
                Version = "1.0",
                InputJson = "{}",
                Status = status,
                CreatedAt = createdAt,
                LastProgressAt = lastProgressAt
            };
}
