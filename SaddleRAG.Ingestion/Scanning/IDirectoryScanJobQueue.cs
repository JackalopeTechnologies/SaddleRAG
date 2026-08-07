// IDirectoryScanJobQueue.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Queues only explicitly requested manual directory scans.</summary>
public interface IDirectoryScanJobQueue
{
    Task<DirectoryScanQueueResult> QueueAsync(string libraryId,
                                              string? profile = null,
                                              CancellationToken ct = default);
}
