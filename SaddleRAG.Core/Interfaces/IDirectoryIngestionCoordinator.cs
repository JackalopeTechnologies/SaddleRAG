// IDirectoryIngestionCoordinator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Owns the atomic publication boundary for a manual directory scan.</summary>
public interface IDirectoryIngestionCoordinator
{
    Task<DirectoryIngestionResult> RunAsync(DirectoryIngestionRequest request,
                                            Action<DirectoryScanProgress>? onProgress,
                                            CancellationToken ct = default);
}
