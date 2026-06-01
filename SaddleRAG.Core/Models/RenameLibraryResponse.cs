// RenameLibraryResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Response from ILibraryRepository.RenameAsync. Counts is null
///     when Outcome is Collision or NotFound.
/// </summary>
public sealed record RenameLibraryResponse(
    RenameLibraryOutcome Outcome,
    RenameLibraryResult? Counts);
