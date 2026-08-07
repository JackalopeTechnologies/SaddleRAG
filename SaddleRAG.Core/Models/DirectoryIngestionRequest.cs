// DirectoryIngestionRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Inputs captured when a user explicitly queues a directory scan.</summary>
public sealed record DirectoryIngestionRequest
{
    public required string LibraryId { get; init; }

    public required string Version { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }

    public required string ScanRunId { get; init; }

    /// <summary>
    ///     Immutable directory registration captured when this manual scan
    ///     was queued. The coordinator and pipeline use this same revision.
    /// </summary>
    public required DirectoryLibraryDefinition Definition { get; init; }

    public string? Profile { get; init; }
}
