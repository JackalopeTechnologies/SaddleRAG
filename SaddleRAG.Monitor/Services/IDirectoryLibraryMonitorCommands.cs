// IDirectoryLibraryMonitorCommands.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Explicit user actions available on the Directory Libraries page.
///     Merely resolving this service performs no action.
/// </summary>
public interface IDirectoryLibraryMonitorCommands
{
    Task<DirectoryAccessTestResult> TestAccessAsync(string rootPath,
                                                    bool recursive,
                                                    CancellationToken ct = default);

    Task<DirectoryRegistrationResult> RegisterAsync(DirectoryRegistrationRequest request,
                                                     string? profile,
                                                     CancellationToken ct = default);

    Task<DirectoryScanQueueResult> ScanAsync(string libraryId,
                                             string? profile,
                                             CancellationToken ct = default);
}
