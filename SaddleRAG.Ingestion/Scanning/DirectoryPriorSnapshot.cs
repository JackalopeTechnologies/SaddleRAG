// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

internal sealed class DirectoryPriorSnapshot
{
    internal DirectoryPriorSnapshot(IReadOnlyDictionary<string, PriorDirectoryDocument> documents,
                                    DirectoryPathIdentity pathIdentity)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(pathIdentity);
        mDocuments = new Dictionary<string, PriorDirectoryDocument>(documents, pathIdentity.Comparer);
    }

    private readonly Dictionary<string, PriorDirectoryDocument> mDocuments;

    internal bool TryGet(string normalizedRelativePath, out PriorDirectoryDocument? document) =>
        mDocuments.TryGetValue(normalizedRelativePath, out document);

    internal void Remove(string normalizedRelativePath)
    {
        _ = mDocuments.Remove(normalizedRelativePath);
    }

    internal static DirectoryPriorSnapshot Empty(DirectoryPathIdentity pathIdentity) =>
        new(new Dictionary<string, PriorDirectoryDocument>(pathIdentity.Comparer), pathIdentity);
}
