// IDirectoryScanFileSystem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Filesystem seam used to make safety outcomes deterministic.</summary>
public interface IDirectoryScanFileSystem
{
    DirectoryPathResult InspectPath(string fullPath);

    DirectoryEnumerationResult EnumerateDirectory(string fullPath);

    Task<StableFileReadResult> ReadStableFileAsync(string fullPath,
                                                   long maxFileBytes,
                                                   CancellationToken cancellationToken = default);
}
