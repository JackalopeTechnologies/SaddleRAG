// IDocUrlResolver.cs

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
///     Resolves a documentation URL from package metadata.
/// </summary>
public interface IDocUrlResolver

{
    /// <summary>
    ///     The package ecosystem this resolver handles (e.g., "nuget", "npm", "pypi").
    /// </summary>

    string EcosystemId { get; }


    /// <summary>
    ///     Resolves the best available documentation URL for the given package metadata.
    /// </summary>
    Task<DocUrlResolution> ResolveAsync(PackageMetadata metadata, CancellationToken ct = default);
}
