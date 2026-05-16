// RankingSettings.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     Configuration knobs for hybrid retrieval and reranking. Bound from
///     the "Ranking" section of appsettings.json.
/// </summary>
public class RankingSettings
{
    /// <summary>
    ///     Weight applied to the BM25 score when blending with vector
    ///     similarity. Hybrid score is (1 - Bm25Weight) * vector +
    ///     Bm25Weight * bm25, both normalized to [0,1].
    /// </summary>
    public float Bm25Weight { get; set; } = DefaultBm25Weight;

    /// <summary>
    ///     Multiplier applied to maxResults to decide how many vector
    ///     candidates to fetch before hybrid blending. Larger values
    ///     improve recall on large corpora at the cost of more local work.
    /// </summary>
    public int VectorCandidateMultiplier { get; set; } = DefaultVectorCandidateMultiplier;

    /// <summary>
    ///     Minimum number of vector candidates to fetch regardless of the
    ///     caller's requested result count.
    /// </summary>
    public int MinVectorCandidateCount { get; set; } = DefaultMinVectorCandidateCount;

    /// <summary>
    ///     Maximum number of top hybrid candidates to send through the
    ///     local reranker. Remaining candidates stay hybrid-only.
    /// </summary>
    public int MaxReRankCandidates { get; set; } = DefaultMaxReRankCandidates;

    /// <summary>
    ///     Threshold for the SymbolExtractor's prose-mention backstop.
    ///     A capitalized identifier appearing this many times in prose
    ///     (outside code fences) survives extraction even when no other
    ///     keep rule fires. Lower → more recall, more noise.
    /// </summary>
    public int ProseMentionThreshold { get; set; } = DefaultProseMentionThreshold;

    /// <summary>
    ///     Active reranker strategy. Mutable at runtime via the
    ///     set_rerank_strategy MCP tool; SearchTools and the dispatcher
    ///     both read this property per-call, so writes flow through
    ///     immediately. <see cref="ReRankerStrategy.Off" /> skips
    ///     reranking entirely (NoOpReRanker pass-through). <see cref="ReRankerStrategy.Onnx" />
    ///     dispatches to <c>OnnxReRanker</c>, which scores via the active
    ///     entry in <c>OnnxSettings.RerankerModels</c>, or falls back to
    ///     pass-through if <c>OnnxSettings.ActiveRerankerModel</c> resolves
    ///     to null (e.g., set to <c>"none"</c>). The legacy <c>Llm</c> and
    ///     <c>CrossEncoder</c> values were removed in Phase 5 of the ONNX
    ///     migration.
    /// </summary>
    public ReRankerStrategy ReRankerStrategy { get; set; } = ReRankerStrategy.Off;

    /// <summary>
    ///     Configuration section name in appsettings.
    /// </summary>
    public const string SectionName = "Ranking";

    public const float DefaultBm25Weight = 0.4f;
    public const int DefaultVectorCandidateMultiplier = 5;
    public const int DefaultMinVectorCandidateCount = 50;
    public const int DefaultMaxReRankCandidates = 25;
    public const int DefaultProseMentionThreshold = 3;
}
