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
    /// <summary>Ownership schema understood by the managed-artifact protocol.</summary>
    public const int CurrentClaimSchemaVersion = 1;

    public required string Id { get; init; }

    public required string GridFsId { get; init; }

    public required long ByteLength { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    ///     Ownership protocol understood by the current writer. Null denotes
    ///     legacy metadata, which is deliberately never auto-deleted.
    /// </summary>
    public int? ClaimSchemaVersion { get; init; }

    /// <summary>Exact revision claims for schema-managed artifacts.</summary>
    public IReadOnlyList<DocumentArtifactClaimRecord> Claims { get; init; } = [];

    /// <summary>
    ///     Token for an idempotent physical-deletion attempt. Claim writers
    ///     cannot attach to a tombstoned artifact.
    /// </summary>
    public string? DeletionId { get; init; }

    public DateTime? DeletionPreparedAtUtc { get; init; }
}
