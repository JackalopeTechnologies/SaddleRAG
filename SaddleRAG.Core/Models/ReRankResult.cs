// ReRankResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single result from the re-ranking pass.
/// </summary>
public record ReRankResult
{
    /// <summary>
    ///     The chunk being scored.
    /// </summary>
    public required DocChunk Chunk { get; init; }

    /// <summary>
    ///     Relevance score from the cross-encoder (higher = more relevant).
    /// </summary>
    public required float RelevanceScore { get; init; }
}
