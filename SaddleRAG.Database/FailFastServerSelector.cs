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
///     Runs ahead of the driver's own server selection and lets an MCP tool call
///     stop waiting on a database that is known to be down. It throws
///     <see cref="DatabaseUnavailableException" /> only when all of these hold:
///     <list type="bullet">
///         <item>
///             the operation runs inside a tool call (<see cref="DatabaseFailFastScope" />);
///             crawls, startup and other background work keep the normal wait;
///         </item>
///         <item>
///             the database has been unreachable for the whole grace window (the client's
///             server-selection timeout), so the first 30 s of any outage — including
///             SaddleRAG starting before MongoDB — behave exactly as before;
///         </item>
///         <item>
///             the driver re-checked within the last second and failed. Otherwise the
///             selector lets the operation wait, which makes the driver re-check at once:
///             a recovered database is then used straight away, and a still-dead one fails
///             on that check — a call during an outage costs one re-check, not 30 s.
///         </item>
///     </list>
///     A call that starts late in the grace window fails once the window ends rather
///     than waiting a full timeout of its own.
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
        bool failFast = DatabaseFailFastScope.IsActive &&
                        mTracker.HasBeenUnreachableFor(mGraceWindow) &&
                        mTracker.HeartbeatFailedWithin(smRecheckWindow);
        if (failFast)
            throw new DatabaseUnavailableException(mTracker.Endpoint, mTracker.LastHeartbeatException);

        return servers;
    }

    public override string ToString() => nameof(FailFastServerSelector);

    private static readonly TimeSpan smRecheckWindow = TimeSpan.FromSeconds(seconds: 1);
}
