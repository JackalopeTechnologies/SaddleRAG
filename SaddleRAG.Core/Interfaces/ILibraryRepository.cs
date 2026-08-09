// ILibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Core.Enums;

#endregion

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Data access for library and library version records.
/// </summary>
public interface ILibraryRepository
{
    /// <summary>
    ///     Get all libraries.
    /// </summary>
    Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Get a library by its unique identifier.
    /// </summary>
    Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Create or update a library record.
    /// </summary>
    Task UpsertLibraryAsync(LibraryRecord library, CancellationToken ct = default);

    /// <summary>
    ///     Atomically replace a library summary only when every expected
    ///     summary field still matches.
    /// </summary>
    Task<bool> TryReplaceLibrarySummaryAsync(LibraryRecord expected,
                                             LibraryRecord replacement,
                                             CancellationToken ct = default);

    /// <summary>
    ///     Atomically delete a library summary only when every expected
    ///     summary field still matches.
    /// </summary>
    Task<bool> TryDeleteLibrarySummaryAsync(LibraryRecord expected,
                                            CancellationToken ct = default);

    /// <summary>
    ///     Get version metadata for a specific library version.
    /// </summary>
    Task<LibraryVersionRecord?> GetVersionAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Get all indexed versions for a library, sorted descending by ScrapedAt.
    /// </summary>
    Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Get every version currently in the requested publication state.
    /// </summary>
    Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsByPublicationStateAsync(
        VersionPublicationState publicationState,
        CancellationToken ct = default);

    /// <summary>
    ///     Store version metadata after a scrape completes.
    /// </summary>
    Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default);

    /// <summary>
    ///     Atomically claim a missing version for one receiver-local package
    ///     import. Existing versions are never replaced.
    /// </summary>
    Task<bool> TryClaimImportVersionAsync(LibraryVersionRecord buildingVersion,
                                          string importOperationId,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Publish a package-imported version only while the receiver-local
    ///     import operation still owns its building row.
    /// </summary>
    Task<bool> TryPublishImportVersionAsync(LibraryVersionRecord publishedVersion,
                                            string importOperationId,
                                            CancellationToken ct = default);

    /// <summary>
    ///     Atomically claim a missing or failed directory version for one scan
    ///     run. Published and building versions are never replaced.
    /// </summary>
    Task<DirectoryVersionClaimResult> TryClaimDirectoryVersionAsync(
        LibraryVersionRecord buildingVersion,
        CancellationToken ct = default);

    /// <summary>
    ///     Publish a directory version only when the expected scan run still
    ///     owns its building lease.
    /// </summary>
    Task<bool> TryPublishDirectoryVersionAsync(LibraryVersionRecord publishedVersion,
                                               string scanRunId,
                                               CancellationToken ct = default);

    /// <summary>
    ///     Atomically qualify version cleanup for the scan run that currently
    ///     owns the building or published directory version.
    /// </summary>
    Task<bool> TryBeginDirectoryVersionCleanupAsync(string libraryId,
                                                    string version,
                                                    string scanRunId,
                                                    CancellationToken ct = default);

    /// <summary>
    ///     Record a failed directory version without replacing a version that
    ///     has since been claimed by another scan run.
    /// </summary>
    Task<bool> TryRecordDirectoryVersionFailureAsync(LibraryVersionRecord failedVersion,
                                                     string scanRunId,
                                                     CancellationToken ct = default);

    /// <summary>
    ///     Delete a specific version of a library. Removes the LibraryVersions row,
    ///     then either deletes the Library row (if no versions remain) or repoints
    ///     CurrentVersion to the next-most-recent Published version. Deleting the
    ///     last Published version is refused while unpublished rows remain.
    /// </summary>
    Task<DeleteVersionResult> DeleteVersionAsync(string libraryId, string version, CancellationToken ct = default);

    /// <summary>
    ///     Delete a complete library and all its versions.
    ///     Iterates through all versions and calls DeleteVersionAsync for each,
    ///     then ensures the Library row is deleted.
    /// </summary>
    Task<long> DeleteAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Direct repository renames are intentionally rejected. Call
    ///     <see cref="ILibraryRenameService.RenameLibraryAsync" /> so the
    ///     durable mode fence and recovery operation remain active.
    /// </summary>
    Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default);

    /// <summary>
    ///     Direct repository version renames are intentionally rejected. Call
    ///     <see cref="ILibraryRenameService.RenameVersionAsync" /> so the
    ///     durable mode fence and recovery operation remain active.
    /// </summary>
    Task<RenameLibraryResponse> RenameVersionAsync(string libraryId,
                                                   string oldVersion,
                                                   string newVersion,
                                                   CancellationToken ct = default);

    /// <summary>
    ///     Mark a library version as suspect, recording the reasons and evaluation timestamp.
    /// </summary>
    Task SetSuspectAsync(string libraryId,
                         string version,
                         IReadOnlyList<string> reasons,
                         CancellationToken ct = default);

    /// <summary>
    ///     Clear the suspect flag on a library version, resetting reasons and updating the evaluation timestamp.
    /// </summary>
    Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default);
}
