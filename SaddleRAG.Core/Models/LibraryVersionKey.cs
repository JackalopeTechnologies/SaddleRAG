// LibraryVersionKey.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Composite key identifying a single (LibraryId, Version) tuple.
///     Used by orphan-detection paths that need to compare distinct pairs
///     across collections without falling back to anonymous tuples.
/// </summary>
public readonly record struct LibraryVersionKey(string LibraryId, string Version);
