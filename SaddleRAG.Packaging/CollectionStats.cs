// CollectionStats.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Current size statistics for a MongoDB collection.
/// </summary>
public sealed record CollectionStats(
    string Collection,
    long Count,
    long Size,
    long StorageSize,
    long TotalIndexSize);
