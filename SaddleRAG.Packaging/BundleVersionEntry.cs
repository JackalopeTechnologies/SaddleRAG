// BundleVersionEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Per-version metadata in the manifest. Encoder fields decide the
///     importer's match/mismatch behavior. Blob sha256s key by the
///     zip entry path relative to the bundle root.
/// </summary>
public sealed record BundleVersionEntry
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("embeddingProviderId")]
    public required string EmbeddingProviderId { get; init; }

    [JsonPropertyName("embeddingModelName")]
    public required string EmbeddingModelName { get; init; }

    [JsonPropertyName("embeddingDimensions")]
    public required int EmbeddingDimensions { get; init; }

    [JsonPropertyName("pageCount")]
    public required int PageCount { get; init; }

    [JsonPropertyName("chunkCount")]
    public required int ChunkCount { get; init; }

    [JsonPropertyName("bm25HasGridFs")]
    public required bool Bm25HasGridFs { get; init; }

    [JsonPropertyName("blobs")]
    public required IReadOnlyDictionary<string, BlobInfo> Blobs { get; init; }
}
