// BlobInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json.Serialization;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Integrity descriptor for a single blob inside a bundle. The importer
///     recomputes the hash on read and aborts before any DB write if it
///     does not match.
/// </summary>
public sealed record BlobInfo
{
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }
}
