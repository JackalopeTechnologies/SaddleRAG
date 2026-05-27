// CollectionStats.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
