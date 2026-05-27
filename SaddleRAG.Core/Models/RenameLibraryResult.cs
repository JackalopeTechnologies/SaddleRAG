// RenameLibraryResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models;

/// <summary>
///     Per-collection update counts from a library rename.
/// </summary>
public sealed record RenameLibraryResult(
    long Libraries,
    long Versions,
    long Chunks,
    long Pages,
    long Profiles,
    long Indexes,
    long Bm25Shards,
    long ExcludedSymbols,
    long ScrapeJobs);
