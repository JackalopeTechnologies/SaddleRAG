// CachedMongoClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     One MongoDB client per connection string, paired with the tracker that
///     follows its cluster's reachability.
/// </summary>
internal sealed record CachedMongoClient(IMongoClient Client, DatabaseReachabilityTracker Reachability);
