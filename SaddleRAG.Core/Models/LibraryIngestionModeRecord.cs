// LibraryIngestionModeRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Durable source-mode ownership plus the current operation lease for one library identifier.
///     A reserved legacy record may be reconciled from durable evidence; once committed,
///     the mode is immutable and expiry permits same-mode recovery only.
/// </summary>
public sealed record LibraryIngestionModeRecord
{
    public required string Id { get; init; }

    public required LibraryIngestionMode Mode { get; init; }

    public LibraryIngestionOwnershipState OwnershipState { get; init; }

    public string? LeaseOwnerToken { get; init; }

    public DateTime? LeaseExpiresAtUtc { get; init; }

    public required DateTime ReservedAtUtc { get; init; }

    public DateTime? CommittedAtUtc { get; init; }

    /// <summary>
    ///     Crash-recovery marker for a multi-library rename. Normal operations cannot acquire
    ///     a record while this is set; only the exact rename operation can recover it.
    /// </summary>
    public string? PendingRenameOperationId { get; init; }

    public required DateTime UpdatedAtUtc { get; init; }
}
