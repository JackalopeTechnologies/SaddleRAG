// ReRankerStrategy.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Selects which reranker SearchTools.search_docs uses after hybrid
///     scoring (vector ∥ BM25). Two states: Off (recommended for
///     identifier-shape queries; fastest, no plateau artifacts) and Onnx
///     (in-process cross-encoder via Microsoft.ML.OnnxRuntime, default
///     model mxbai-rerank-base-v1, ~150ms batched on CPU).
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
    ///     In-process ONNX cross-encoder reranker. Loads the model named
    ///     by <c>OnnxSettings.ActiveRerankerModel</c> (default
    ///     <c>mxbai-rerank-base-v1</c>) via Microsoft.ML.OnnxRuntime and
    ///     scores (query, doc) pairs in a batched single forward pass per
    ///     search. Produces continuous floats — no plateau artifacts.
    /// </summary>
    Onnx
}
