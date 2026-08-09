// IDirectoryScanner.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Explicit-only boundary for a standalone directory preview.</summary>
public interface IDirectoryScanner
{
    Task<DirectoryScanReport> ScanAsync(DirectoryScanRequest request,
                                        CancellationToken cancellationToken = default);
}
