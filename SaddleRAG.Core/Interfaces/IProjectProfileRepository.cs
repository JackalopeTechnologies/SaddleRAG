// IProjectProfileRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Interfaces;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

/// <summary>
///     Lifecycle operations for project profiles that reference ingested libraries.
/// </summary>
public interface IProjectProfileRepository
{
    /// <summary>
    ///     Count project profiles that reference one exact library identifier.
    /// </summary>
    Task<long> CountIngestedPackageReferencesAsync(string libraryId, CancellationToken ct = default);

    /// <summary>
    ///     Remove one exact library identifier from every project profile.
    /// </summary>
    Task<long> RemoveIngestedPackageAsync(string libraryId, CancellationToken ct = default);
}
