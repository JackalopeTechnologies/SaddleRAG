// DirectoryIngestionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Sanitized result of one explicitly requested directory ingestion.</summary>
public sealed record DirectoryIngestionResult(string Status,
                                              string LibraryId,
                                              string Version,
                                              int DocumentsProcessed = 0,
                                              int PagesIndexed = 0,
                                              int ChunksIndexed = 0,
                                              string? ReasonCode = null,
                                              string? Detail = null)
{
    public IReadOnlyList<DirectoryScanFileFailure> FileFailures { get; init; } = [];
}
