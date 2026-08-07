// ILibraryDeletionService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

/// <summary>
///     One ordered cascade used by every version/library deletion entry point.
/// </summary>
public interface ILibraryDeletionService
{
    Task<LibraryDeletionResult> DeleteVersionAsync(string? profile,
                                                    string libraryId,
                                                    string version,
                                                    CancellationToken ct = default);

    Task<LibraryDeletionResult> DeleteLibraryAsync(string? profile,
                                                    string libraryId,
                                                    CancellationToken ct = default);
}
