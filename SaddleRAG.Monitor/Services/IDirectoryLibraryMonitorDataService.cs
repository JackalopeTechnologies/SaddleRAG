// IDirectoryLibraryMonitorDataService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Monitor.Services;

/// <summary>Read-only source for the Directory Libraries page.</summary>
public interface IDirectoryLibraryMonitorDataService
{
    Task<IReadOnlyList<DirectoryLibraryMonitorRow>> ListAsync(string? profile,
                                                              CancellationToken ct = default);
}
