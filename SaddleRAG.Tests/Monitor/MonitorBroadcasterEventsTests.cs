// MonitorBroadcasterEventsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Diagnostics;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorBroadcasterEventsTests
{
    [Fact]
    public void RecordJobStartedRaisesJobStartedEvent()
    {
        var bcast = new MonitorBroadcaster();
        JobStartedEvent? captured = null;
        bcast.JobStarted += e => captured = e;

        bcast.RecordJobStarted("job-1", "alpha", "1.0.0", "https://docs.example/");

        Assert.NotNull(captured);
        if (captured is not null)
        {
            Assert.Equal("job-1", captured.JobId);
            Assert.Equal("alpha", captured.LibraryId);
            Assert.Equal("1.0.0", captured.Version);
            Assert.Equal("https://docs.example/", captured.RootUrl);
        }
    }

    [Fact]
    public void RecordJobCompletedRaisesJobCompletedEvent()
    {
        var bcast = new MonitorBroadcaster();
        bcast.RecordJobStarted("job-1", "alpha", "1.0.0", "https://docs.example/");
        JobCompletedEvent? captured = null;
        bcast.JobCompleted += e => captured = e;

        bcast.RecordJobCompleted("job-1", indexedPageCount: 42);

        Assert.NotNull(captured);
        if (captured is not null)
        {
            Assert.Equal("job-1", captured.JobId);
            Assert.Equal(expected: 42, captured.IndexedPageCount);
            Assert.NotNull(captured.FinalCounters);
        }
    }

    [Fact]
    public void RecordJobFailedRaisesJobFailedEvent()
    {
        var bcast = new MonitorBroadcaster();
        bcast.RecordJobStarted("job-1", "alpha", "1.0.0", "https://docs.example/");
        JobFailedEvent? captured = null;
        bcast.JobFailed += e => captured = e;

        bcast.RecordJobFailed("job-1", "boom");

        Assert.NotNull(captured);
        if (captured is not null)
        {
            Assert.Equal("job-1", captured.JobId);
            Assert.Equal("boom", captured.ErrorMessage);
        }
    }

    [Fact]
    public void RecordJobCancelledRaisesJobCancelledEvent()
    {
        var bcast = new MonitorBroadcaster();
        bcast.RecordJobStarted("job-1", "alpha", "1.0.0", "https://docs.example/");
        JobCancelledEvent? captured = null;
        bcast.JobCancelled += e => captured = e;

        bcast.RecordJobCancelled("job-1");

        Assert.NotNull(captured);
        if (captured is not null)
        {
            Assert.Equal("job-1", captured.JobId);
            Assert.NotNull(captured.PartialCounters);
        }
    }

    [Fact]
    public void RecordSuspectFlagRaisesSuspectFlagRaisedEvent()
    {
        var bcast = new MonitorBroadcaster();
        SuspectFlagEvent? captured = null;
        bcast.SuspectFlagRaised += e => captured = e;

        bcast.RecordSuspectFlag("job-1", "alpha", "1.0.0", new[] { "low confidence", "thin docs" });

        Assert.NotNull(captured);
        if (captured is not null)
        {
            Assert.Equal("job-1", captured.JobId);
            Assert.Equal("alpha", captured.LibraryId);
            Assert.Equal("1.0.0", captured.Version);
            Assert.Equal(new[] { "low confidence", "thin docs" }, captured.Reasons);
        }
    }

    [Fact]
    public void SubscriberExceptionDoesNotPropagate()
    {
        var bcast = new MonitorBroadcaster();
        bcast.JobFailed += _ => throw new InvalidOperationException("subscriber blew up");

        bcast.RecordJobStarted("job-1", "alpha", "1.0.0", "https://docs.example/");

        var ex = Record.Exception(() => bcast.RecordJobFailed("job-1", "boom"));
        Assert.Null(ex);
    }
}
