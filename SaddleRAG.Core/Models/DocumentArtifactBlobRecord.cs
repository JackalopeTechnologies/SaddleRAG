// DocumentArtifactBlobRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Content-addressed pointer from a canonical SHA-256 hash to bytes in
///     the dedicated document-artifact GridFS bucket.
/// </summary>
public record DocumentArtifactBlobRecord
{
    public required string Id { get; init; }

    public required string GridFsId { get; init; }

    public required long ByteLength { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}
