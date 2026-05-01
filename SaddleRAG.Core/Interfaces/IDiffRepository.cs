// IDiffRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
}
