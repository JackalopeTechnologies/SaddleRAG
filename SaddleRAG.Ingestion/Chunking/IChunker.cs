// IChunker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Chunking;

/// <summary>
///     Minimal seam for the streaming-pipeline chunk stage: split a
///     classified page into retrieval-sized <see cref="DocChunk" /> records.
///     The concrete <see cref="CategoryAwareChunker" /> implements this; the
///     interface exists so the chunk stage can be unit-tested with a stub
///     and so failure modes (chunker throws) are exercisable without a real
///     chunker.
/// </summary>
internal interface IChunker
{
    /// <summary>
    ///     Chunk <paramref name="page" /> into one or more <see cref="DocChunk" />
    ///     records (without embeddings). If <paramref name="libraryProfile" />
    ///     is supplied, identifier-aware extraction runs against it.
    /// </summary>
    IReadOnlyList<DocChunk> Chunk(PageRecord page, LibraryProfile? libraryProfile = null);
}
