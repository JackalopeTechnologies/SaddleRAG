// MonitorBroadcasterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Diagnostics;
#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorBroadcasterTests
{
    [Fact]
    public void RecordFetchAddsToRecentFeedAndIncrementsCounter()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-1", "lib", "1.0", "https://x.com/");
        broadcaster.RecordFetch("job-1", "https://x.com/page");

        var snapshot = broadcaster.GetJobSnapshot("job-1");
        Assert.NotNull(snapshot);
        if (snapshot is not null)
        {
            Assert.Equal(1, snapshot.Counters.PagesFetched);
            Assert.Single(snapshot.RecentFetches);
            Assert.Equal("https://x.com/page", snapshot.RecentFetches[0].Url);
        }
    }

    [Fact]
    public void RecentFeedCapAt50Entries()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-2", "lib", "1.0", "https://y.com/");

        for (var i = 0; i < 60; i++)
            broadcaster.RecordFetch("job-2", $"https://y.com/{i}");

        var snapshot = broadcaster.GetJobSnapshot("job-2");
        Assert.NotNull(snapshot);
        if (snapshot is not null)
            Assert.Equal(50, snapshot.RecentFetches.Count);
    }

    [Fact]
    public void GetJobSnapshotReturnsNullForUnknownJob()
    {
        var broadcaster = new MonitorBroadcaster();
        Assert.Null(broadcaster.GetJobSnapshot("no-such-job"));
    }

    [Fact]
    public void JobCompletedClearsActiveJobFromBroadcaster()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-3", "lib", "1.0", "https://z.com/");
        broadcaster.RecordJobCompleted("job-3", indexedPageCount: 5);

        Assert.Null(broadcaster.GetJobSnapshot("job-3"));
    }

    [Fact]
    public void SubscriberReceivesTickEvent()
    {
        var broadcaster   = new MonitorBroadcaster();
        JobTickEvent? got = null;
        broadcaster.Subscribe("job-4", tick => { got = tick; return Task.CompletedTask; });

        broadcaster.RecordJobStarted("job-4", "lib", "1.0", "https://w.com/");
        broadcaster.RecordFetch("job-4", "https://w.com/page");
        broadcaster.BroadcastTick("job-4");

        Assert.NotNull(got);
        if (got is not null)
        {
            Assert.Equal("job-4", got.JobId);
            Assert.Equal(1, got.Counters.PagesFetched);
        }
    }
}
