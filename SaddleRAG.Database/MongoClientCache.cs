// MongoClientCache.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Concurrent;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Hands out one MongoDB client per connection string, each wired with a
///     reachability tracker and the fail-fast server selector.
///     <para>
///         Sharing is deliberate. The driver pools connections per distinct client
///         configuration, and the configuration now carries per-client callbacks, so a
///         client built per context would give every context its own pool and its own
///         heartbeat monitor. Before the callbacks existed, contexts with the same
///         connection string already shared one pool; caching the client keeps that.
///     </para>
/// </summary>
internal static class MongoClientCache
{
    public static CachedMongoClient GetOrCreate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        var cached = smClients.GetOrAdd(connectionString,
                                        key => new Lazy<CachedMongoClient>(() => Create(key)))
                              .Value;
        return cached;
    }

    private static CachedMongoClient Create(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        var tracker = new DatabaseReachabilityTracker(TimeProvider.System);
        var selector = new FailFastServerSelector(tracker, settings.ServerSelectionTimeout);

        settings.ClusterConfigurator = builder =>
                                           builder.Subscribe<ClusterDescriptionChangedEvent>(e => tracker.Observe(e.NewDescription))
                                                  .ConfigureCluster(cluster => cluster.With(preServerSelector: selector));

        var cached = new CachedMongoClient(new MongoClient(settings), tracker);
        return cached;
    }

    private static readonly ConcurrentDictionary<string, Lazy<CachedMongoClient>> smClients =
        new ConcurrentDictionary<string, Lazy<CachedMongoClient>>(StringComparer.Ordinal);
}
