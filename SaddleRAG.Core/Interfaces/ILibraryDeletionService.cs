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

    /// <summary>
    ///     Deletes one version while retaining the exact in-flight job that reports the result.
    /// </summary>
    Task<LibraryDeletionResult> DeleteVersionPreservingJobAsync(string? profile,
                                                                 string libraryId,
                                                                 string version,
                                                                 string preservedJobId,
                                                                 CancellationToken ct = default);

    Task<LibraryDeletionResult> DeleteLibraryAsync(string? profile,
                                                    string libraryId,
                                                    CancellationToken ct = default);

    /// <summary>
    ///     Deletes one library while retaining the exact in-flight job that reports the result.
    /// </summary>
    Task<LibraryDeletionResult> DeleteLibraryPreservingJobAsync(string? profile,
                                                                 string libraryId,
                                                                 string preservedJobId,
                                                                 CancellationToken ct = default);

    /// <summary>
    ///     Deletes one version while retaining the exact source-mode operation lease
    ///     already owned by the caller.
    /// </summary>
    Task<LibraryDeletionResult> DeleteVersionUnderModeLeaseAsync(
        string? profile,
        string libraryId,
        string version,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes a library while retaining the exact source-mode operation lease
    ///     already owned by the caller.
    /// </summary>
    Task<LibraryDeletionResult> DeleteLibraryUnderModeLeaseAsync(
        string? profile,
        string libraryId,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default);

    /// <summary>
    ///     Removes one failed directory-scan candidate while retaining the
    ///     publication and source-mode leases already owned by its coordinator.
    /// </summary>
    Task<LibraryDeletionResult> DeleteScanCandidateUnderLeaseAsync(
        string? profile,
        string libraryId,
        string version,
        IDirectoryPublicationLease publicationLease,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct = default);
}
