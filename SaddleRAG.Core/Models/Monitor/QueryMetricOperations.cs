// QueryMetricOperations.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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

    /// <summary>Vector-search-provider call against the indexed chunks.</summary>
    public const string VectorSearch = "vector_search";

    /// <summary>Re-ranker call after hybrid blending.</summary>
    public const string Rerank = "rerank";
}
