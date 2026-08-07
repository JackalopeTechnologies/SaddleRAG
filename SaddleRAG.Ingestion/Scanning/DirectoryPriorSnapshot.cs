// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

internal sealed record DirectoryPriorSnapshot(
    IReadOnlyDictionary<string, PriorDirectoryDocument> Documents)
{
    public static DirectoryPriorSnapshot Empty { get; } = new(
        new Dictionary<string, PriorDirectoryDocument>(StringComparer.OrdinalIgnoreCase));
}
