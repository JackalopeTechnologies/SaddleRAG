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
            Assert.Equal(expected: 1, snapshot.Counters.PagesFetched);
            Assert.Single(snapshot.RecentFetches);
            Assert.Equal("https://x.com/page", snapshot.RecentFetches[index: 0].Url);
        }
    }

    [Fact]
    public void RecentFeedCapAt50Entries()
    {
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("job-2", "lib", "1.0", "https://y.com/");

        for(var i = 0; i < 60; i++)
            broadcaster.RecordFetch("job-2", $"https://y.com/{i}");

        var snapshot = broadcaster.GetJobSnapshot("job-2");
        Assert.NotNull(snapshot);
        if (snapshot is not null)
            Assert.Equal(expected: 50, snapshot.RecentFetches.Count);
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
        var broadcaster = new MonitorBroadcaster();
        JobTickEvent? got = null;
        broadcaster.Subscribe("job-4",
                              tick =>
                              {
                                  got = tick;
                                  return Task.CompletedTask;
                              }
                             );

        broadcaster.RecordJobStarted("job-4", "lib", "1.0", "https://w.com/");
        broadcaster.RecordFetch("job-4", "https://w.com/page");
        broadcaster.BroadcastTick("job-4");

        Assert.NotNull(got);
        if (got is not null)
        {
            Assert.Equal("job-4", got.JobId);
            Assert.Equal(expected: 1, got.Counters.PagesFetched);
        }
    }

    [Fact]
    public async Task ConcurrentRecordFetchNoDataRace()
    {
        const int FetchCount = 100;
        var ct = TestContext.Current.CancellationToken;
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("concurrent-fetch", "lib", "1.0", "https://c.com/");

        var tasks = Enumerable.Range(start: 0, FetchCount)
                              .Select(i => Task.Run(() => broadcaster.RecordFetch("concurrent-fetch",
                                                             $"https://c.com/{i}"
                                                        ),
                                                    ct
                                                   )
                                     );
        await Task.WhenAll(tasks);

        var snapshot = broadcaster.GetJobSnapshot("concurrent-fetch");
        Assert.NotNull(snapshot);
        if (snapshot is not null)
            Assert.Equal(FetchCount, snapshot.Counters.PagesFetched);
    }

    [Fact]
    public async Task ConcurrentSubscribeAndBroadcastNoException()
    {
        const int ThreadCount = 50;
        var ct = TestContext.Current.CancellationToken;
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("concurrent-sub", "lib", "1.0", "https://s.com/");
        broadcaster.RecordFetch("concurrent-sub", "https://s.com/page");

        var subscribeTasks = Enumerable.Range(start: 0, ThreadCount)
                                       .Select(_ => Task.Run(() =>
                                                                 broadcaster.Subscribe("concurrent-sub",
                                                                          tick => Task.CompletedTask
                                                                     ),
                                                             ct
                                                            )
                                              );
        var broadcastTasks = Enumerable.Range(start: 0, ThreadCount)
                                       .Select(_ => Task.Run(() => broadcaster.BroadcastTick("concurrent-sub"), ct));

        await Task.WhenAll(subscribeTasks.Concat(broadcastTasks));
    }

    [Fact]
    public async Task ConcurrentJobCompletedAndGetSnapshotNoException()
    {
        const int ReadCount = 200;
        var ct = TestContext.Current.CancellationToken;
        var broadcaster = new MonitorBroadcaster();
        broadcaster.RecordJobStarted("concurrent-complete", "lib", "1.0", "https://d.com/");
        broadcaster.RecordFetch("concurrent-complete", "https://d.com/page");

        var readTasks = Enumerable.Range(start: 0, ReadCount)
                                  .Select(_ => Task.Run(() => broadcaster.GetJobSnapshot("concurrent-complete"), ct));
        var completeTask = Task.Run(() => broadcaster.RecordJobCompleted("concurrent-complete", indexedPageCount: 1),
                                    ct
                                   );

        await Task.WhenAll(readTasks.Append(completeTask));

        Assert.Null(broadcaster.GetJobSnapshot("concurrent-complete"));
    }
}
