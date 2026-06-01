// DeleteVersionResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Outcome of a single-version delete: how many version rows were
///     removed, whether the parent Library row was cascade-deleted
///     (because no versions remained), and the new currentVersion if
///     one had to be repointed.
/// </summary>
public sealed record DeleteVersionResult(
    long VersionsDeleted,
    bool LibraryRowDeleted,
    string? CurrentVersionRepointedTo);
