// ReRankerStrategy.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Selects which reranker strategy SearchTools.search_docs uses after
///     hybrid scoring (vector ∥ BM25). Default is Off — the reviewer's
///     bench showed that the legacy LLM categorical reranker hurts
///     identifier queries more often than it helps; flip to a non-Off
///     strategy only after the bench harness confirms a net-positive
///     nDCG@5 for your corpus.
/// </summary>
public enum ReRankerStrategy
{
    /// <summary>
    ///     No reranking. Hybrid score (vector ∥ BM25) is the final score.
    ///     Recommended default — fastest, no plateau artifacts, no
    ///     identifier-query regression.
    /// </summary>
    Off,

    /// <summary>
    ///     Categorical LLM reranker (Ollama prompt-based). Default model
    ///     is phi4-mini:3.8b (Microsoft, Western supply chain). Currently
    ///     dispatches to NoOp in ToggleableReRanker until calibration is
    ///     verified — small instruction-tuned LLMs tend to plateau on
    ///     the 5-bucket score scale (1.0/0.8/0.5/0.2/0.0) without a
    ///     bench harness to tune the prompt against.
    /// </summary>
    Llm,

    /// <summary>
    ///     Cross-encoder-style reranker hosting Mixedbread mxbai-rerank-
    ///     large-v2 on Ollama. The community Ollama port was originally
    ///     hosted as a generate model emitting continuous floats, but
    ///     has since been republished as embed-only — so this strategy
    ///     also currently dispatches to NoOp. Re-enable when a real
    ///     cross-encoder runtime is wired up (e.g. HuggingFace TEI
    ///     sidecar with an /api/rerank endpoint).
    /// </summary>
    CrossEncoder
}
