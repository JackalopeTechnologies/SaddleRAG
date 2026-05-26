// BundleManifest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Root-level manifest serialized as `manifest.json` inside the bundle.
///     The importer reads this first and refuses to proceed if
///     <see cref="ManifestVersion" /> exceeds the version it knows about.
/// </summary>
public sealed record BundleManifest
{
    public required int ManifestVersion { get; init; }
    public required string ExporterVersion { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required BundleLibraryInfo Library { get; init; }
    public required IReadOnlyList<BundleVersionEntry> Versions { get; init; }
}

