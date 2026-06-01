// VectorSearchResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single result from vector similarity search.
/// </summary>
public record VectorSearchResult

{
    /// <summary>
    ///     The matched chunk.
    /// </summary>

    public required DocChunk Chunk { get; init; }


    /// <summary>
    ///     Similarity score (higher = more similar).
    /// </summary>

    public required float Score { get; init; }
}
