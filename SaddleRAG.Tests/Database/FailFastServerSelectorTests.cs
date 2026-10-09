// FailFastServerSelectorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Servers;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class FailFastServerSelectorTests
{
    [Fact]
    public void PassesServersThroughWhileTheDatabaseIsReachable()
    {
        var selector = new FailFastServerSelector(new DatabaseReachabilityTracker(new ManualTimeProvider(smStart)), smGrace);
        var connected = MakeServer(heartbeatException: null, ServerState.Connected);

        using var _ = DatabaseFailFastScope.Begin();
        var selected = selector.SelectServers(MakeCluster(connected), [connected]);

        Assert.Equal([connected], selected);
    }

    [Fact]
    public void NeverFailsFastOutsideAToolCall()
    {
        (var selector, var clock, var tracker) = MakeSelector();
        tracker.Observe(MakeCluster(MakeServer(new IOException("refused"), ServerState.Disconnected)));
        clock.Advance(smGrace);
        var freshFailure = MakeCluster(MakeServer(new IOException("refused again"), ServerState.Disconnected));

        var selected = selector.SelectServers(freshFailure, []);

        Assert.Empty(selected);
    }

    [Fact]
    public void KeepsWaitingDuringTheGraceWindowSoABootRaceStillSucceeds()
    {
        (var selector, var clock, var tracker) = MakeSelector();
        tracker.Observe(MakeCluster(MakeServer(new IOException("refused"), ServerState.Disconnected)));
        clock.Advance(TimeSpan.FromSeconds(seconds: 29));
        var freshFailure = MakeCluster(MakeServer(new IOException("refused again"), ServerState.Disconnected));

        using var _ = DatabaseFailFastScope.Begin();
        var selected = selector.SelectServers(freshFailure, []);

        Assert.Empty(selected);
    }

    [Fact]
    public void WaitsForAFreshHeartbeatBeforeFailingSoARecoveredDatabaseIsNoticed()
    {
        (var selector, var clock, var tracker) = MakeSelector();
        var staleFailure = MakeCluster(MakeServer(new IOException("refused"), ServerState.Disconnected));
        tracker.Observe(staleFailure);
        clock.Advance(smGrace);

        using var _ = DatabaseFailFastScope.Begin();
        var selected = selector.SelectServers(staleFailure, []);

        Assert.Empty(selected);
    }

    [Fact]
    public void FailsFastInsideAToolCallOnceUnreachableForTheGraceWindowAndJustRechecked()
    {
        (var selector, var clock, var tracker) = MakeSelector();
        tracker.Observe(MakeCluster(MakeServer(new IOException("refused"), ServerState.Disconnected)));
        clock.Advance(smGrace);
        var refusedAgain = new IOException("refused again");
        var freshFailure = MakeCluster(MakeServer(refusedAgain, ServerState.Disconnected));

        using var _ = DatabaseFailFastScope.Begin();
        var ex = Assert.Throws<DatabaseUnavailableException>(() => selector.SelectServers(freshFailure, []));

        Assert.Equal("localhost:27017", ex.Endpoint);
        Assert.Same(refusedAgain, ex.InnerException);
        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public void StopsFailingFastOnceTheToolCallEnds()
    {
        (var selector, var clock, var tracker) = MakeSelector();
        tracker.Observe(MakeCluster(MakeServer(new IOException("refused"), ServerState.Disconnected)));
        clock.Advance(smGrace);
        var freshFailure = MakeCluster(MakeServer(new IOException("refused again"), ServerState.Disconnected));

        var scope = DatabaseFailFastScope.Begin();
        scope.Dispose();
        var selected = selector.SelectServers(freshFailure, []);

        Assert.Empty(selected);
    }

    private static (FailFastServerSelector Selector, ManualTimeProvider Clock, DatabaseReachabilityTracker Tracker) MakeSelector()
    {
        var clock = new ManualTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        return (new FailFastServerSelector(tracker, smGrace), clock, tracker);
    }

    private static ClusterDescription MakeCluster(params ServerDescription[] servers) =>
        new ClusterDescription(smClusterId,
                               directConnection: false,
                               dnsMonitorException: null,
                               ClusterType.Unknown,
                               servers
                              );

    private static ServerDescription MakeServer(Exception? heartbeatException, ServerState state) =>
        new ServerDescription(new ServerId(smClusterId, smLocalhost),
                              smLocalhost,
                              heartbeatException: heartbeatException,
                              state: state,
                              type: state == ServerState.Connected ? ServerType.Standalone : ServerType.Unknown
                             );

    private static readonly ClusterId smClusterId = new ClusterId(value: 1);
    private static readonly DnsEndPoint smLocalhost = new DnsEndPoint("localhost", port: 27017);
    private static readonly DateTimeOffset smStart = new DateTimeOffset(2026, 10, 8, 23, 50, 0, TimeSpan.Zero);
    private static readonly TimeSpan smGrace = TimeSpan.FromSeconds(seconds: 30);
}
