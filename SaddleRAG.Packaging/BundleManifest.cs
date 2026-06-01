// BundleManifest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Root-level manifest serialized as `manifest.json` inside the bundle.
///     The importer reads this first and refuses to proceed if
///     <see cref="ManifestVersion" /> exceeds the version it knows about.
/// </summary>
public sealed record BundleManifest
{
    [JsonPropertyName("manifestVersion")]
    public required int ManifestVersion { get; init; }

    [JsonPropertyName("exporterVersion")]
    public required string ExporterVersion { get; init; }

    /// <summary>
    ///     Exporters must populate this from <c>DateTime.UtcNow</c>;
    ///     importers treat unspecified Kind as UTC.
    /// </summary>
    [JsonPropertyName("createdUtc")]
    public required DateTime CreatedUtc { get; init; }

    [JsonPropertyName("library")]
    public required BundleLibraryInfo Library { get; init; }

    /// <summary>
    ///     Integrity descriptors for blobs that live at the root of the bundle
    ///     (library.json plus any future top-level blobs). Per-version blobs are
    ///     in <see cref="BundleVersionEntry.Blobs" /> instead.
    /// </summary>
    [JsonPropertyName("blobs")]
    public required IReadOnlyDictionary<string, BlobInfo> Blobs { get; init; }

    [JsonPropertyName("versions")]
    public required IReadOnlyList<BundleVersionEntry> Versions { get; init; }
}
