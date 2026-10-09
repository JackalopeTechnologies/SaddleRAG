// DatabaseReachabilityTrackerTests.cs
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

public sealed class DatabaseReachabilityTrackerTests
{
    [Fact]
    public void NewTrackerIsNotUnreachable()
    {
        var tracker = new DatabaseReachabilityTracker(new ManualTimeProvider(smStart));

        Assert.False(tracker.IsUnreachable);
        Assert.False(tracker.HasBeenUnreachableFor(TimeSpan.Zero));
        Assert.False(tracker.HeartbeatFailedWithin(TimeSpan.FromSeconds(seconds: 1)));
    }

    [Fact]
    public void ServerWithNoHeartbeatResultYetIsNotUnreachable()
    {
        var tracker = new DatabaseReachabilityTracker(new ManualTimeProvider(smStart));

        tracker.Observe(MakeCluster(MakeServer(smLocalhost, heartbeatException: null, ServerState.Disconnected)));

        Assert.False(tracker.IsUnreachable);
    }

    [Fact]
    public void EveryServerFailingItsHeartbeatMarksTheDatabaseUnreachableAtThatEndpoint()
    {
        var tracker = new DatabaseReachabilityTracker(new ManualTimeProvider(smStart));
        var refused = new IOException("No connection could be made because the target machine actively refused it.");

        tracker.Observe(MakeCluster(MakeServer(smLocalhost, refused, ServerState.Disconnected)));

        Assert.True(tracker.IsUnreachable);
        Assert.Equal("localhost:27017", tracker.Endpoint);
        Assert.Same(refused, tracker.LastHeartbeatException);
    }

    [Fact]
    public void UnreachableDurationCountsFromTheFirstFailedHeartbeatNotTheLatest()
    {
        var clock = new ManualTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected)));

        clock.Advance(TimeSpan.FromSeconds(seconds: 10));
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused again"), ServerState.Disconnected)));

        Assert.True(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 10)));
        Assert.False(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 11)));
    }

    [Fact]
    public void UnreachableDurationIgnoresAWallClockCorrection()
    {
        var clock = new ManualTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected)));

        clock.StepWallClock(TimeSpan.FromHours(hours: 1));
        clock.Advance(TimeSpan.FromSeconds(seconds: 1));

        Assert.True(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 1)));
        Assert.False(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 2)));
    }

    [Fact]
    public void ConnectedServerClearsUnreachableAndALaterOutageRestartsTheClock()
    {
        var clock = new ManualTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected)));

        clock.Advance(TimeSpan.FromSeconds(seconds: 60));
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, heartbeatException: null, ServerState.Connected)));
        Assert.False(tracker.IsUnreachable);

        clock.Advance(TimeSpan.FromSeconds(seconds: 10));
        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected)));
        clock.Advance(TimeSpan.FromSeconds(seconds: 5));

        Assert.True(tracker.IsUnreachable);
        Assert.True(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 5)));
        Assert.False(tracker.HasBeenUnreachableFor(TimeSpan.FromSeconds(seconds: 6)));
    }

    [Fact]
    public void ANewHeartbeatFailureIsFreshButSeeingTheSameOneAgainIsNot()
    {
        var clock = new ManualTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        var first = MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected));
        tracker.Observe(first);

        clock.Advance(TimeSpan.FromSeconds(seconds: 5));
        tracker.Observe(first);
        Assert.False(tracker.HeartbeatFailedWithin(TimeSpan.FromSeconds(seconds: 1)));

        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused again"), ServerState.Disconnected)));
        Assert.True(tracker.HeartbeatFailedWithin(TimeSpan.FromSeconds(seconds: 1)));
    }

    [Fact]
    public void OneReachableServerKeepsAMultiServerClusterReachable()
    {
        var tracker = new DatabaseReachabilityTracker(new ManualTimeProvider(smStart));

        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected),
                                    MakeServer(new DnsEndPoint("db2", port: 27018), heartbeatException: null, ServerState.Connected)
                                   ));

        Assert.False(tracker.IsUnreachable);
    }

    [Fact]
    public void EndpointListsEveryServerInStableOrderWhenAllAreUnreachable()
    {
        var tracker = new DatabaseReachabilityTracker(new ManualTimeProvider(smStart));

        tracker.Observe(MakeCluster(MakeServer(smLocalhost, new IOException("refused"), ServerState.Disconnected),
                                    MakeServer(new IPEndPoint(IPAddress.Loopback, port: 27018), new IOException("refused"), ServerState.Disconnected)
                                   ));

        Assert.True(tracker.IsUnreachable);
        Assert.Equal("127.0.0.1:27018, localhost:27017", tracker.Endpoint);
    }

    private static ClusterDescription MakeCluster(params ServerDescription[] servers) =>
        new ClusterDescription(smClusterId,
                               directConnection: false,
                               dnsMonitorException: null,
                               ClusterType.Unknown,
                               servers
                              );

    private static ServerDescription MakeServer(EndPoint endPoint, Exception? heartbeatException, ServerState state) =>
        new ServerDescription(new ServerId(smClusterId, endPoint),
                              endPoint,
                              heartbeatException: heartbeatException,
                              state: state,
                              type: state == ServerState.Connected ? ServerType.Standalone : ServerType.Unknown
                             );

    private static readonly ClusterId smClusterId = new ClusterId(value: 1);
    private static readonly DnsEndPoint smLocalhost = new DnsEndPoint("localhost", port: 27017);
    private static readonly DateTimeOffset smStart = new DateTimeOffset(2026, 10, 8, 23, 50, 0, TimeSpan.Zero);
}
