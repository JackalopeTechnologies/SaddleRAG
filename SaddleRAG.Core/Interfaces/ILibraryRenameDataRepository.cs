// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Fenced, idempotent MongoDB mutation steps used only by the durable
///     library rename coordinator.
/// </summary>
public interface ILibraryRenameDataRepository
{
    Task<RenameLibraryOutcome> PreflightLibraryRenameAsync(string sourceLibraryId,
                                                            string targetLibraryId,
                                                            CancellationToken ct = default);

    Task<RenameLibraryOutcome> PreflightVersionRenameAsync(string libraryId,
                                                            string sourceVersion,
                                                            string targetVersion,
                                                            CancellationToken ct = default);

    Task PrepareDirectoryDefinitionsAsync(LibraryRenameOperationRecord operation,
                                          CancellationToken ct = default);

    Task<RenameLibraryResult> ApplyLibraryRenameAsync(LibraryRenameOperationRecord operation,
                                                       CancellationToken ct = default);

    Task<RenameLibraryResult> ApplyVersionRenameAsync(LibraryRenameOperationRecord operation,
                                                       CancellationToken ct = default);

    Task FinalizeDirectoryDefinitionsAsync(LibraryRenameOperationRecord operation,
                                           CancellationToken ct = default);

    Task<bool> IsFinalizedAsync(LibraryRenameOperationRecord operation,
                                CancellationToken ct = default);
}
