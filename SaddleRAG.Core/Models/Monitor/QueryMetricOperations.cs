// QueryMetricOperations.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Canonical operation labels recorded into <see cref="QuerySample.Operation" />.
///     Centralized so the monitor performance UI and the call sites stay in sync.
/// </summary>
public static class QueryMetricOperations
{
    /// <summary>Top-level <c>search_docs</c> MCP tool invocation (wraps embed + vector + bm25 + rerank).</summary>
    public const string SearchDocs = "search_docs";

    /// <summary>Embedding-provider call to convert a query string into a vector.</summary>
    public const string EmbedQuery = "embed_query";

    /// <summary>One-shot local LLM query-planning call before retrieval.</summary>
    public const string QueryPlan = "query_plan";

    /// <summary>Vector-search-provider call against the indexed chunks.</summary>
    public const string VectorSearch = "vector_search";

    /// <summary>Re-ranker call after hybrid blending.</summary>
    public const string Rerank = "rerank";

    /// <summary>
    ///     Identifier-shape fast path inside <c>search_docs</c>. Records both
    ///     success (with the number of QualifiedName-matched chunks injected,
    ///     possibly zero) and failure (with the exception type as the note)
    ///     so an SLO can alert on a high failure rate that the
    ///     LogLevel.Warning emit alone wouldn't surface.
    /// </summary>
    public const string IdentifierFastPath = "identifier_fast_path";
}
