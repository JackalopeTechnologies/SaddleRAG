// FailFastServerSelector.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Clusters.ServerSelectors;
using MongoDB.Driver.Core.Servers;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Runs ahead of the driver's own server selection. Once the database has been
///     unreachable for a whole grace window (the client's server-selection timeout),
///     waiting again cannot help — every caller in that window already waited it
///     out — so it fails the operation at once with a
///     <see cref="DatabaseUnavailableException" /> instead of letting it wait the full
///     timeout. Inside the window it changes nothing, so SaddleRAG starting before
///     MongoDB behaves exactly as it always has.
/// </summary>
internal sealed class FailFastServerSelector : IServerSelector
{
    public FailFastServerSelector(DatabaseReachabilityTracker tracker, TimeSpan graceWindow)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentOutOfRangeException.ThrowIfLessThan(graceWindow, TimeSpan.Zero);
        mTracker = tracker;
        mGraceWindow = graceWindow;
    }

    private readonly TimeSpan mGraceWindow;
    private readonly DatabaseReachabilityTracker mTracker;

    public IEnumerable<ServerDescription> SelectServers(ClusterDescription cluster,
                                                        IEnumerable<ServerDescription> servers)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(servers);

        // Observe here as well as from the driver's change event: the selector can
        // run before the event handler sees the same description.
        mTracker.Observe(cluster);
        if (mTracker.HasBeenUnreachableFor(mGraceWindow))
            throw new DatabaseUnavailableException(mTracker.Endpoint, mTracker.LastHeartbeatException);

        return servers;
    }

    public override string ToString() => nameof(FailFastServerSelector);
}
