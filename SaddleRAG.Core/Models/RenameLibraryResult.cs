// RenameLibraryResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
