// ILibraryRenameService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Coordinates database and vector-index identity changes for library renames.</summary>
public interface ILibraryRenameService
{
    Task<RenameLibraryResponse> RenameLibraryAsync(string? profile,
                                                   string oldLibraryId,
                                                   string newLibraryId,
                                                   CancellationToken ct = default);

    Task<RenameLibraryResponse> RenameVersionAsync(string? profile,
                                                   string libraryId,
                                                   string oldVersion,
                                                   string newVersion,
                                                   CancellationToken ct = default);
}
