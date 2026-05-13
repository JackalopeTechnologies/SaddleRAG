// OnnxGraphOptimizationLevel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Graph-optimization level applied to ONNX Runtime
///     <c>SessionOptions</c>. Bound from
///     <c>Onnx.GraphOptimizationLevel</c> in appsettings.json
///     (case-insensitive string). Maps 1:1 to ORT's native
///     <c>GraphOptimizationLevel</c> via
///     <c>OnnxEmbeddingProvider.ParseGraphOptimizationLevel</c>.
///
///     <para>
///     <see cref="All" /> triggers a <c>SimplifiedLayerNormFusion</c> bug
///     in ORT 1.26 against the precision-cast nodes both nomic-fp16 and
///     mxbai exports contain. Do not change the operator-facing default
///     of <see cref="Basic" /> without testing every entry in the
///     embedding + reranker registries.
///     </para>
/// </summary>
public enum OnnxGraphOptimizationLevel
{
    /// <summary>Maps to ORT's <c>ORT_DISABLE_ALL</c>. No optimization.</summary>
    Disable,

    /// <summary>Maps to ORT's <c>ORT_ENABLE_BASIC</c>. Safe default.</summary>
    Basic,

    /// <summary>Maps to ORT's <c>ORT_ENABLE_EXTENDED</c>.</summary>
    Extended,

    /// <summary>
    ///     Maps to ORT's <c>ORT_ENABLE_ALL</c>. <strong>Triggers a
    ///     SimplifiedLayerNormFusion bug in ORT 1.26 against the
    ///     precision-cast nodes both nomic-fp16 and mxbai exports
    ///     contain.</strong> Avoid in production.
    /// </summary>
    All
}
