// MonitorJobServiceDurationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Pins the <see cref="MonitorJobService.JobHistoryRow.Duration" />
///     computed property's three branches: null when the job never
///     started, falls back to <see cref="DateTime.UtcNow" /> for running
///     jobs, and uses <see cref="MonitorJobService.JobHistoryRow.CompletedAt" />
///     for finished jobs. The /monitor/jobs index page renders this value
///     directly; a regression here surfaces immediately on the UI as a
///     blank/incorrect duration column.
/// </summary>
public sealed class MonitorJobServiceDurationTests
{
    private static MonitorJobService.JobHistoryRow Row(DateTime? startedAt, DateTime? completedAt) =>
        new MonitorJobService.JobHistoryRow
            {
                JobId = "j1",
                Type = JobType.Scrape,
                Status = "Completed",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                StartedAt = startedAt,
                CompletedAt = completedAt
            };

    [Fact]
    public void DurationIsNullWhenStartedAtIsNull()
    {
        var row = Row(startedAt: null, completedAt: null);
        Assert.Null(row.Duration);
    }

    [Fact]
    public void DurationUsesCompletedAtMinusStartedAtForFinishedJobs()
    {
        var startedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var completedAt = startedAt.AddSeconds(45);

        var row = Row(startedAt, completedAt);

        Assert.Equal(TimeSpan.FromSeconds(45), row.Duration);
    }

    [Fact]
    public void DurationFallsBackToUtcNowWhenCompletedAtIsNullForRunningJobs()
    {
        var startedAt = DateTime.UtcNow.AddSeconds(-3);
        var row = Row(startedAt, completedAt: null);

        var duration = row.Duration;

        Assert.NotNull(duration);
        // Duration should be > ~2s and < ~10s — wall-clock fuzz between row
        // creation and assertion is allowed but bounded.
        Assert.InRange(duration.Value.TotalSeconds, low: 2, high: 10);
    }
}
