// IDirectoryPublicationLease.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Durable per-library lifecycle lease. Directory scans and destructive
///     version or library operations hold the same lease so their mutations
///     cannot overlap. The publication-era name is retained for source and
///     BSON compatibility.
/// </summary>
public interface IDirectoryPublicationLease : IAsyncDisposable
{
    /// <summary>Library whose publication is leased.</summary>
    string LibraryId { get; }

    /// <summary>Scan run that owns the lease.</summary>
    string ScanRunId { get; }

    /// <summary>
    ///     Unique identity of the captured directory registration. Null only
    ///     for legacy definitions created before incarnation fencing.
    /// </summary>
    string? RegistrationIncarnationId { get; }

    /// <summary>Captured directory registration revision.</summary>
    long RegistrationRevision { get; }

    /// <summary>
    ///     Canceled when renewal can no longer prove that this instance owns
    ///     the lease. Long-running work must always observe this token.
    /// </summary>
    CancellationToken OwnershipLostToken { get; }

    /// <summary>
    ///     Extend this lease only while its owner still holds the same,
    ///     unexpired registration incarnation and revision.
    /// </summary>
    ValueTask<bool> TryRenewAsync(CancellationToken ct = default);
}
