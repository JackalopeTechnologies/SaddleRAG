// LibraryDeletionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Per-store counts returned by the shared library deletion cascade.
/// </summary>
public sealed record LibraryDeletionResult(
    long Libraries,
    long Versions,
    long Chunks,
    long Pages,
    long Profiles,
    long Indexes,
    long Bm25Shards,
    long ExcludedSymbols,
    long AuditEntries,
    string? CurrentVersionRepointedTo = null,
    long DocumentRevisions = 0,
    long SubjectAssignments = 0,
    long SubjectCatalogs = 0,
    long Diffs = 0,
    long Jobs = 0,
    long ProjectProfiles = 0);
