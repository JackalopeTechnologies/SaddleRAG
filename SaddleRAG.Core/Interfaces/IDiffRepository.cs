// IDiffRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;

#endregion


#pragma warning disable STR0010 // Interface methods cannot validate parameters


namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Data access for version diff records.
/// </summary>
public interface IDiffRepository

{
    /// <summary>
    ///     Store a version diff record.
    /// </summary>
    Task UpsertDiffAsync(VersionDiffRecord diff, CancellationToken ct = default);


    /// <summary>
    ///     Get a diff between two specific versions.
    /// </summary>
    Task<VersionDiffRecord?> GetDiffAsync(string libraryId,
                                          string fromVersion,
                                          string toVersion,
                                          CancellationToken ct = default);

    /// <summary>
    ///     Delete every comparison that references one library version.
    /// </summary>
    Task<long> DeleteVersionAsync(string libraryId,
                                  string version,
                                  CancellationToken ct = default);

    /// <summary>
    ///     Delete every comparison owned by one library.
    /// </summary>
    Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Enumerate both endpoints of every stored comparison for orphan detection.
    /// </summary>
    Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default);
}
