// BundleVersionEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging;

/// <summary>
///     Per-version metadata in the manifest. Encoder fields decide the
///     importer's match/mismatch behavior. Blob sha256s key by the
///     zip entry path relative to the bundle root.
/// </summary>
public sealed record BundleVersionEntry
{
    public required string Version { get; init; }
    public required string EmbeddingProviderId { get; init; }
    public required string EmbeddingModelName { get; init; }
    public required int EmbeddingDimensions { get; init; }
    public required int PageCount { get; init; }
    public required int ChunkCount { get; init; }
    public required bool Bm25HasGridFs { get; init; }
    public required IReadOnlyDictionary<string, BlobInfo> Blobs { get; init; }
}
