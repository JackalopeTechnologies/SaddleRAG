// PackageMetadata.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

// Available under AGPLv3 (see LICENSE) or a commercial license

// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.


namespace SaddleRAG.Core.Models;

/// <summary>
///     Package metadata fetched from a package registry.
/// </summary>
public record PackageMetadata

{
    /// <summary>
    ///     The package identifier (e.g., "Newtonsoft.Json").
    /// </summary>

    public required string PackageId { get; init; }


    /// <summary>
    ///     The package version string.
    /// </summary>

    public required string Version { get; init; }


    /// <summary>
    ///     The package ecosystem (e.g., "nuget", "npm", "pypi").
    /// </summary>

    public required string EcosystemId { get; init; }


    /// <summary>
    ///     The project home page URL declared in the package metadata.
    /// </summary>

    public string ProjectUrl { get; init; } = string.Empty;


    /// <summary>
    ///     The source repository URL declared in the package metadata.
    /// </summary>

    public string RepositoryUrl { get; init; } = string.Empty;


    /// <summary>
    ///     An explicit documentation URL declared in the package metadata.
    /// </summary>

    public string DocumentationUrl { get; init; } = string.Empty;


    /// <summary>
    ///     Short description of the package.
    /// </summary>

    public string Description { get; init; } = string.Empty;
}
