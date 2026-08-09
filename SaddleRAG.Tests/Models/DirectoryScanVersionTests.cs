// DirectoryScanVersionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Models;

public sealed class DirectoryScanVersionTests
{
    [Fact]
    public void CaptureUsesLocalCalendarDateAndRemainsImmutableAcrossMidnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestMountain", TimeSpan.FromHours(-7), "Test Mountain", "Test Mountain");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 5, 6, 30, 0, TimeSpan.Zero), zone);
        var provider = new DirectoryScanVersionProvider(clock);

        var captured = provider.Capture();
        clock.UtcNow = clock.UtcNow.AddHours(2);

        Assert.Equal("2026-08-04", captured.Value);
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 23, 30, 0, TimeSpan.FromHours(-7)), captured.QueuedAt);
        Assert.Equal("2026-08-04", captured.Value);
    }

    [Fact]
    public void PublishedSameDateRevisionReturnsAlreadyScannedToday()
    {
        var decision = DirectoryScanVersionProvider.DecideSameDate(DocumentRevisionState.Published);

        Assert.False(decision.ShouldScan);
        Assert.Equal("ALREADY_SCANNED_TODAY", decision.Status);
    }

    [Theory]
    [InlineData(DocumentRevisionState.Failed)]
    [InlineData(DocumentRevisionState.Cancelled)]
    public void FailedOrCancelledSameDateRevisionAllowsRetry(DocumentRevisionState revisionState)
    {
        var decision = DirectoryScanVersionProvider.DecideSameDate(revisionState);

        Assert.True(decision.ShouldScan);
        Assert.Equal("RETRY_ALLOWED", decision.Status);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone)
        {
            UtcNow = utcNow;
            LocalTimeZone = localTimeZone;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override TimeZoneInfo LocalTimeZone { get; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
