// DocumentArtifactClaimRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Exact ownership token held inside one managed document artifact.
///     Prepared claims protect bytes before their revision is committed;
///     finalized claims identify durable revision ownership.
/// </summary>
public record DocumentArtifactClaimRecord
{
    public required string ClaimId { get; init; }

    public required string RevisionId { get; init; }

    public required DateTime PreparedAtUtc { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }

    public DateTime? FinalizedAtUtc { get; init; }
}
