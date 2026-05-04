// QueryMetricsRecorderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Diagnostics;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class QueryMetricsRecorderTests
{
    [Fact]
    public void RingBufferCapsAtCapacity()
    {
        var rec = new QueryMetricsRecorder(capacity: 3);
        rec.Record("search", TimeSpan.FromMilliseconds(10), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(20), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(30), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(40), success: true);

        var snap = rec.Snapshot();
        Assert.Equal(expected: 3, snap.RecentSamples.Count);
        Assert.Equal(expected: 20.0, snap.RecentSamples[index: 0].DurationMs, precision: 3);
        Assert.Equal(expected: 40.0, snap.RecentSamples[index: 2].DurationMs, precision: 3);
    }

    [Fact]
    public void PerOperationStatsAreGroupedAndPercentilesAreReasonable()
    {
        var rec = new QueryMetricsRecorder(capacity: 1024);
        for(int i = 1; i <= SampleCount; i++)
            rec.Record("search", TimeSpan.FromMilliseconds(i), success: true);
        rec.Record("embed", TimeSpan.FromMilliseconds(200), success: true);

        var snap = rec.Snapshot();
        var search = snap.PerOperation.Single(o => o.Operation == "search");
        Assert.Equal(SampleCount, search.Count);
        Assert.Equal(expected: 0, search.FailureCount);
        Assert.InRange(search.P50Ms, low: 49, high: 52);
        Assert.InRange(search.P95Ms, low: 94, high: 96);
        Assert.Equal(expected: 100.0, search.MaxMs, precision: 3);
    }

    [Fact]
    public void FailureCountTracksUnsuccessfulSamples()
    {
        var rec = new QueryMetricsRecorder(capacity: 100);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: true);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: false);
        rec.Record("search", TimeSpan.FromMilliseconds(5), success: false);

        var snap = rec.Snapshot();
        var op = snap.PerOperation.Single();
        Assert.Equal(expected: 3, op.Count);
        Assert.Equal(expected: 2, op.FailureCount);
    }

    private const int SampleCount = 100;
}
