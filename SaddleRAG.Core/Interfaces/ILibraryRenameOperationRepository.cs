// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Persists exact, compare-and-swap rename recovery checkpoints.</summary>
public interface ILibraryRenameOperationRepository
{
    Task<LibraryRenameOperationRecord?> GetAsync(string sourceLibraryId,
                                                  CancellationToken ct = default);

    Task<LibraryRenameOperationRecord?> TryBeginAsync(LibraryRenameOperationRecord operation,
                                                       CancellationToken ct = default);

    Task<bool> TryAdvanceAsync(string sourceLibraryId,
                               string operationId,
                               LibraryRenameOperationState expectedState,
                               LibraryRenameOperationState nextState,
                               RenameLibraryResult? counts,
                               DateTime updatedAtUtc,
                               CancellationToken ct = default);

    Task<bool> TryDeleteAsync(string sourceLibraryId,
                              string operationId,
                              LibraryRenameOperationState expectedState,
                              CancellationToken ct = default);
}
