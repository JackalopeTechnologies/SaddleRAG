// EmbedRole.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Whether a piece of text is being embedded as a document (for indexing)
///     or as a query (for retrieval against an index). Asymmetric embedding
///     models (e.g. nomic-embed-text-v1.5) emit different vectors for the
///     two roles via task prefixes like <c>search_document:</c> and
///     <c>search_query:</c>; using the wrong role at retrieval time
///     measurably hurts recall. Symmetric models (e.g. all-MiniLM-L6-v2)
///     ignore the role.
/// </summary>
public enum EmbedRole
{
    /// <summary>
    ///     Text being indexed. Provider applies the document-side task
    ///     prefix configured on the active model (if any).
    /// </summary>
    Document,

    /// <summary>
    ///     Text being used as a search query against indexed documents.
    ///     Provider applies the query-side task prefix configured on the
    ///     active model (if any).
    /// </summary>
    Query
}
