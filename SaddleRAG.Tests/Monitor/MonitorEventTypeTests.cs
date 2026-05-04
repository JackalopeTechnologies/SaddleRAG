// MonitorEventTypeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorEventTypeTests
{
    [Fact]
    public void JobTickEventRoundTripsThroughJson()
    {
        var tick = new JobTickEvent
                       {
                           JobId = "job-1",
                           At = new DateTime(year: 2026,
                                             month: 5,
                                             day: 3,
                                             hour: 0,
                                             minute: 0,
                                             second: 0,
                                             DateTimeKind.Utc
                                            ),
                           Counters = new PipelineCounters { PagesQueued = 10, PagesFetched = 5 },
                           CurrentHost = "example.com",
                           RecentFetches = [new RecentFetch { Url = "https://example.com/a" }],
                           RecentRejects =
                               [
                                   new RecentReject
                                       {
                                           Url = "https://example.com/b",
                                           Reason = "PatternExclude"
                                       }
                               ],
                           ErrorsThisTick = []
                       };

        string json = JsonSerializer.Serialize(tick);
        var roundTrip = JsonSerializer.Deserialize<JobTickEvent>(json);

        Assert.NotNull(roundTrip);
        if (roundTrip is not null)
        {
            Assert.Equal("job-1", roundTrip.JobId);
            Assert.Equal(expected: 10, roundTrip.Counters.PagesQueued);
            Assert.Single(roundTrip.RecentFetches);
            Assert.Single(roundTrip.RecentRejects);
        }
    }

    [Fact]
    public void JobStartedEventRoundTripsThroughJson()
    {
        var evt = new JobStartedEvent
                      {
                          JobId = "job-2",
                          LibraryId = "lib",
                          Version = "1.0",
                          RootUrl = "https://docs.example.com/"
                      };

        string json = JsonSerializer.Serialize(evt);
        var roundTrip = JsonSerializer.Deserialize<JobStartedEvent>(json);

        Assert.NotNull(roundTrip);
        if (roundTrip is not null)
            Assert.Equal("lib", roundTrip.LibraryId);
    }
}
