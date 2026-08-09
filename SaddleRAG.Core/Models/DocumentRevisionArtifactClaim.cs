// DocumentRevisionArtifactClaim.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Exact managed-artifact claim committed with a document revision.</summary>
public record DocumentRevisionArtifactClaim
{
    public required string ArtifactHash { get; init; }

    public required string ClaimId { get; init; }
}
