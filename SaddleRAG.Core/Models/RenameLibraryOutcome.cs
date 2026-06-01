// RenameLibraryOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Result classification for a library rename: success, name collision,
///     or source library not found.
/// </summary>
public enum RenameLibraryOutcome
{
    Renamed,
    Collision,
    NotFound
}
