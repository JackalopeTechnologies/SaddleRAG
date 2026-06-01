// ICollectionCompactor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Driver;

namespace SaddleRAG.Packaging;

/// <summary>
///     Provides MongoDB collection compaction and stats operations.
///     Abstracted to allow mocking in unit tests without needing a real MongoDB.
/// </summary>
public interface ICollectionCompactor
{
    /// <summary>
    ///     The default set of collections that benefit from compact after
    ///     heavy SaddleRAG create/delete-library churn.
    /// </summary>
    IReadOnlyList<string> DefaultHotCollections { get; }

    /// <summary>
    ///     Retrieve current size statistics for a single collection without
    ///     modifying it. Returns zero-valued stats when the collection does
    ///     not exist or is otherwise unreadable.
    /// </summary>
    Task<CollectionStats> GetStatsAsync(IMongoDatabase database,
                                        string collectionName,
                                        CancellationToken ct);

    /// <summary>
    ///     Runs the MongoDB <c>compact</c> command on a single collection and
    ///     returns before/after storage metrics. Records elapsed time and any
    ///     error message when the command fails. Blocks the target collection
    ///     (not the whole DB) while running.
    /// </summary>
    Task<CompactResult> CompactAsync(IMongoDatabase database,
                                     string collectionName,
                                     CancellationToken ct);
}
