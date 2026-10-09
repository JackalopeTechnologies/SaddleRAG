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
        var clock = new MutableTimeProvider(smStart);
        var selector = new FailFastServerSelector(new DatabaseReachabilityTracker(clock), smGrace);
        var connected = MakeServer(heartbeatException: null, ServerState.Connected);

        var selected = selector.SelectServers(MakeCluster(connected), [connected]);

        Assert.Equal([connected], selected);
    }

    [Fact]
    public void KeepsWaitingDuringTheGraceWindowSoABootRaceStillSucceeds()
    {
        var clock = new MutableTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        var selector = new FailFastServerSelector(tracker, smGrace);
        var failed = MakeServer(new IOException("refused"), ServerState.Disconnected);
        tracker.Observe(MakeCluster(failed));

        clock.UtcNow = smStart.AddSeconds(seconds: 29);
        var selected = selector.SelectServers(MakeCluster(failed), []);

        Assert.Empty(selected);
    }

    [Fact]
    public void ThrowsDatabaseUnavailableOnceUnreachableForTheWholeGraceWindow()
    {
        var clock = new MutableTimeProvider(smStart);
        var tracker = new DatabaseReachabilityTracker(clock);
        var selector = new FailFastServerSelector(tracker, smGrace);
        var refused = new IOException("refused");
        var failed = MakeServer(refused, ServerState.Disconnected);
        tracker.Observe(MakeCluster(failed));

        clock.UtcNow = smStart.Add(smGrace);
        var ex = Assert.Throws<DatabaseUnavailableException>(() => selector.SelectServers(MakeCluster(failed), []));

        Assert.Equal("localhost:27017", ex.Endpoint);
        Assert.Same(refused, ex.InnerException);
        Assert.Contains("localhost:27017", ex.Message);
        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public void ObservesTheDescriptionItIsGivenSoItDoesNotDependOnEventOrder()
    {
        var clock = new MutableTimeProvider(smStart);
        var selector = new FailFastServerSelector(new DatabaseReachabilityTracker(clock), smGrace);
        var failed = MakeServer(new IOException("refused"), ServerState.Disconnected);

        var first = selector.SelectServers(MakeCluster(failed), []);
        clock.UtcNow = smStart.Add(smGrace);

        Assert.Empty(first);
        Assert.Throws<DatabaseUnavailableException>(() => selector.SelectServers(MakeCluster(failed), []));
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

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
