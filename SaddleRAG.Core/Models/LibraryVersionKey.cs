// LibraryVersionKey.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Composite key identifying a single (LibraryId, Version) tuple.
///     Used by orphan-detection paths that need to compare distinct pairs
///     across collections without falling back to anonymous tuples.
/// </summary>
public readonly record struct LibraryVersionKey(string LibraryId, string Version);
