// Bm25Posting.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     A single posting in the BM25 inverted index — one chunk's term-frequency
///     contribution for a given term.
/// </summary>
public record Bm25Posting
{
    /// <summary>
    ///     Chunk id this posting belongs to.
    /// </summary>
    public required string ChunkId { get; init; }

    /// <summary>
    ///     How many times the term occurs in this chunk's content.
    /// </summary>
    public required int TermFrequency { get; init; }
}
