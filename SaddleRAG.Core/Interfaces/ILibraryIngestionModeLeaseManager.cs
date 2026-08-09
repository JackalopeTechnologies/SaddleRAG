// ILibraryIngestionModeLeaseManager.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>Acquires per-profile, per-library source-mode leases.</summary>
public interface ILibraryIngestionModeLeaseManager
{
    Task<ILibraryIngestionModeLease?> TryAcquireAsync(string? profile,
                                                       string libraryId,
                                                       LibraryIngestionMode mode,
                                                       CancellationToken ct = default);

    Task<ILibraryIngestionModeLease?> TryAcquireRenameRecoveryAsync(string? profile,
                                                                    string libraryId,
                                                                    LibraryIngestionMode mode,
                                                                    string renameOperationId,
                                                                    CancellationToken ct = default);
}
