// IPackageRegistryClient.cs
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
///     Fetches package metadata from a package registry.
/// </summary>
public interface IPackageRegistryClient
{
    /// <summary>
    ///     The package ecosystem this client targets (e.g., "nuget", "npm", "pypi").
    /// </summary>
    string EcosystemId { get; }

    /// <summary>
    ///     Fetches metadata for the specified package and version.
    ///     Returns <see langword="null" /> when the package is not found.
    /// </summary>
    Task<PackageMetadata?> FetchMetadataAsync(string packageId, string version, CancellationToken ct = default);
}
