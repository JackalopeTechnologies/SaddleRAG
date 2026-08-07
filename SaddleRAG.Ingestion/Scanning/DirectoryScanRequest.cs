// DirectoryScanRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Inputs for one manually invoked, non-publishing directory preview.</summary>
public sealed record DirectoryScanRequest
{
    public required string LibraryId { get; init; }

    public required string ScanRunId { get; init; }

    public required string RootPath { get; init; }

    public bool Recursive { get; init; }

    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];

    public IReadOnlyList<string> ExclusionPatterns { get; init; } = [];

    public long MaxFileBytes { get; init; } = DirectoryScanLimits.DefaultMaxFileBytes;
}
