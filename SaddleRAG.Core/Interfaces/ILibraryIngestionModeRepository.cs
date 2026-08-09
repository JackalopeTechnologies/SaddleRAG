// ILibraryIngestionModeRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Atomically persists source-mode ownership and cross-process operation leases.</summary>
public interface ILibraryIngestionModeRepository
{
    Task<LibraryIngestionModeRecord?> TryAcquireAsync(string libraryId,
                                                       LibraryIngestionMode mode,
                                                       string ownerToken,
                                                       DateTime nowUtc,
                                                       DateTime expiresAtUtc,
                                                       CancellationToken ct = default);

    Task<LibraryIngestionModeRecord?> TryAcquireRenameRecoveryAsync(string libraryId,
                                                                    LibraryIngestionMode mode,
                                                                    string renameOperationId,
                                                                    string ownerToken,
                                                                    DateTime nowUtc,
                                                                    DateTime expiresAtUtc,
                                                                    CancellationToken ct = default);

    Task<LibraryIngestionModeRecord?> GetAsync(string libraryId, CancellationToken ct = default);

    Task<bool> TryRenewAsync(string libraryId,
                             LibraryIngestionMode mode,
                             string ownerToken,
                             DateTime updatedAtUtc,
                             DateTime expiresAtUtc,
                             CancellationToken ct = default);

    Task<bool> TryCommitAsync(string libraryId,
                              LibraryIngestionMode mode,
                              string ownerToken,
                              DateTime committedAtUtc,
                              CancellationToken ct = default);

    Task<bool> TryReconcileReservedModeAsync(string libraryId,
                                             LibraryIngestionMode expectedMode,
                                             LibraryIngestionMode detectedMode,
                                             string ownerToken,
                                             DateTime committedAtUtc,
                                             CancellationToken ct = default);

    Task<bool> TryReleaseAsync(string libraryId,
                               LibraryIngestionMode mode,
                               string ownerToken,
                               DateTime updatedAtUtc,
                               CancellationToken ct = default);

    Task<bool> TryAbandonReservationAsync(string libraryId,
                                          LibraryIngestionMode mode,
                                          string ownerToken,
                                          CancellationToken ct = default);

    Task<bool> TryDeleteOwnershipAsync(string libraryId,
                                       LibraryIngestionMode mode,
                                       string ownerToken,
                                       CancellationToken ct = default);

    Task<bool> TryMarkPendingRenameAsync(string libraryId,
                                         LibraryIngestionMode mode,
                                         string ownerToken,
                                         string renameOperationId,
                                         DateTime updatedAtUtc,
                                         CancellationToken ct = default);

    Task<bool> TryClearPendingRenameAsync(string libraryId,
                                          LibraryIngestionMode mode,
                                          string ownerToken,
                                          string renameOperationId,
                                          DateTime updatedAtUtc,
                                          CancellationToken ct = default);

    Task<bool> HasAnyLibraryDataAsync(string libraryId, CancellationToken ct = default);

    Task<LibraryIngestionDataEvidence> GetLibraryDataEvidenceAsync(string libraryId,
                                                                   CancellationToken ct = default);
}
