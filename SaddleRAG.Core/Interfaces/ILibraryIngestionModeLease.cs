// ILibraryIngestionModeLease.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>An exact-owner, auto-renewed operation lease over durable ingestion-mode ownership.</summary>
public interface ILibraryIngestionModeLease : IAsyncDisposable
{
    string LibraryId { get; }

    LibraryIngestionMode Mode { get; }

    LibraryIngestionOwnershipState OwnershipStateAtAcquisition { get; }

    CancellationToken OwnershipLostToken { get; }

    ValueTask<bool> TryRenewAsync(CancellationToken ct = default);

    ValueTask<bool> TryCommitAsync(CancellationToken ct = default);

    ValueTask<bool> TryReconcileReservedModeAsync(LibraryIngestionMode detectedMode,
                                                  CancellationToken ct = default);

    ValueTask<bool> TryAbandonReservationAsync(CancellationToken ct = default);

    ValueTask<bool> TryDeleteOwnershipAsync(CancellationToken ct = default);

    ValueTask<bool> TryMarkPendingRenameAsync(string renameOperationId, CancellationToken ct = default);

    ValueTask<bool> TryClearPendingRenameAsync(string renameOperationId, CancellationToken ct = default);
}
