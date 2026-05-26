// ReRankResult.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

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
    ///     Relevance score from the reranker (higher = more relevant).
    ///     Cross-encoder implementations (e.g. <c>OnnxReRanker</c>) must
    ///     sigmoid-map raw model logits into the (0, 1) range before
    ///     populating this field. <c>SearchTools.ApplyRerankerOrderingAsync</c>
    ///     uses this score directly as <c>RankedResult.FinalScore</c> for
    ///     reranked items, which sit above the pass-through tail (whose
    ///     <c>FinalScore</c> is the [0, 1] hybrid score) so the two tiers
    ///     don't fight on incompatible scales. Pass-through implementations
    ///     (e.g. <c>NoOpReRanker</c>) already emit synthetic descending
    ///     [0, 1] scores and need no transformation.
    /// </summary>

    public required float RelevanceScore { get; init; }
}
