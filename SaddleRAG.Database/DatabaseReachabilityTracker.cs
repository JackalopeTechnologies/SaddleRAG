// DatabaseReachabilityTracker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Servers;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Follows the MongoDB driver's view of one cluster and answers whether the
///     database is known to be unreachable, at which endpoint, and for how long.
///     <para>
///         "Unreachable" means every known server is disconnected and its most recent
///         heartbeat failed. A server that has not reported a heartbeat result yet is
///         not unreachable — that is a fresh process racing MongoDB's own start.
///     </para>
/// </summary>
public sealed class DatabaseReachabilityTracker
{
    public DatabaseReachabilityTracker(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        mTimeProvider = timeProvider;
    }

    private readonly Lock mLock = new Lock();
    private readonly TimeProvider mTimeProvider;
    private string mEndpoint = string.Empty;
    private Exception? mLastHeartbeatException;
    private DateTimeOffset? mUnreachableSince;

    /// <summary>
    ///     True while every known server's latest heartbeat has failed.
    /// </summary>
    public bool IsUnreachable
    {
        get
        {
            bool result;
            lock(mLock)
            {
                result = mUnreachableSince.HasValue;
            }

            return result;
        }
    }

    /// <summary>
    ///     The cluster's server addresses as <c>host:port</c>, comma-separated;
    ///     empty until the driver has described the cluster.
    /// </summary>
    public string Endpoint
    {
        get
        {
            string result;
            lock(mLock)
            {
                result = mEndpoint;
            }

            return result;
        }
    }

    /// <summary>
    ///     The heartbeat failure behind the current outage; null while reachable.
    /// </summary>
    public Exception? LastHeartbeatException
    {
        get
        {
            Exception? result;
            lock(mLock)
            {
                result = mLastHeartbeatException;
            }

            return result;
        }
    }

    /// <summary>
    ///     Records the driver's latest description of the cluster. The outage clock
    ///     starts at the first description in which every server is unreachable and
    ///     stops at the first one in which any server is not.
    /// </summary>
    public void Observe(ClusterDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        var servers = description.Servers;
        bool unreachable = servers.Count > 0 && servers.All(IsServerUnreachable);
        Exception? heartbeatException = servers.Select(s => s.HeartbeatException).FirstOrDefault(e => e != null);

        lock(mLock)
        {
            if (servers.Count > 0)
            {
                // Sorted: the driver reorders servers between descriptions, and the
                // text is shown to people, so it must not shuffle.
                mEndpoint = string.Join(MongoEndPointText.Separator,
                                        servers.Select(s => MongoEndPointText.Format(s.EndPoint))
                                               .Order(StringComparer.Ordinal)
                                       );
            }

            if (unreachable)
            {
                mUnreachableSince ??= mTimeProvider.GetUtcNow();
                mLastHeartbeatException = heartbeatException;
            }
            else
            {
                mUnreachableSince = null;
                mLastHeartbeatException = null;
            }
        }
    }

    /// <summary>
    ///     True when the database has been continuously unreachable for at least
    ///     <paramref name="duration" />.
    /// </summary>
    public bool HasBeenUnreachableFor(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        bool result;
        lock(mLock)
        {
            result = mUnreachableSince.HasValue && mTimeProvider.GetUtcNow() - mUnreachableSince.Value >= duration;
        }

        return result;
    }

    private static bool IsServerUnreachable(ServerDescription server) =>
        server.State == ServerState.Disconnected && server.HeartbeatException != null;
}
