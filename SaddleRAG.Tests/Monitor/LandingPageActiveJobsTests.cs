// LandingPageActiveJobsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Pages;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class LandingPageActiveJobsTests
{
    [Fact]
    public void RebuildFromIdsResolvesSnapshotsAndDropsMissingIds()
    {
        var bcast = new FakeBroadcaster();
        bcast.SetSnapshot("job-1", MakeSnap("job-1", queued: 10));
        bcast.SetSnapshot("job-2", MakeSnap("job-2", queued: 5));
        var page = new TestableLandingPage { BroadcasterForTest = bcast };

        page.RebuildFromIds(new[] { "job-1", "job-2", "job-missing" });

        Assert.Equal(2, page.ActiveJobsForTest.Count);
        Assert.Contains(page.ActiveJobsForTest, j => j.JobId == "job-1");
        Assert.Contains(page.ActiveJobsForTest, j => j.JobId == "job-2");
        Assert.DoesNotContain(page.ActiveJobsForTest, j => j.JobId == "job-missing");
    }

    private static JobTickSnapshot MakeSnap(string jobId, int queued) =>
        new JobTickSnapshot
            {
                JobId = jobId,
                Counters = new PipelineCounters { PagesQueued = queued },
                RecentFetches = Array.Empty<RecentFetch>(),
                RecentRejects = Array.Empty<RecentReject>(),
                RecentErrors = Array.Empty<RecentError>()
            };

    private sealed class TestableLandingPage : LandingPageBase
    {
        public IReadOnlyList<JobTickSnapshot> ActiveJobsForTest => ActiveJobSnapshots;
        public IMonitorBroadcaster? BroadcasterForTest
        {
            get => Broadcaster;
            set => Broadcaster = value;
        }
    }
}

internal sealed class FakeBroadcaster : IMonitorBroadcaster
{
    private readonly Dictionary<string, JobTickSnapshot> mSnaps = new();
    public void SetSnapshot(string id, JobTickSnapshot s) => mSnaps[id] = s;
    public JobTickSnapshot? GetJobSnapshot(string jobId) => mSnaps.GetValueOrDefault(jobId);
    public IReadOnlyList<string> GetActiveJobIds() => mSnaps.Keys.ToList();
    public void RecordJobStarted(string j, string l, string v, string r) {}
    public void RecordFetch(string j, string u) {}
    public void RecordReject(string j, string u, string r) {}
    public void RecordError(string j, string m) {}
    public void RecordPageClassified(string j) {}
    public void RecordChunkGenerated(string j) {}
    public void RecordChunkEmbedded(string j) {}
    public void RecordPageCompleted(string j) {}
    public void RecordJobCompleted(string j, int n) {}
    public void RecordJobFailed(string j, string m) {}
    public void RecordJobCancelled(string j) {}
    public void RecordSuspectFlag(string j, string l, string v, IReadOnlyList<string> r) {}
    public void Subscribe(string j, Func<JobTickEvent, Task> h) {}
    public void Unsubscribe(string j, Func<JobTickEvent, Task> h) {}
    public void BroadcastTick(string j) {}
}
