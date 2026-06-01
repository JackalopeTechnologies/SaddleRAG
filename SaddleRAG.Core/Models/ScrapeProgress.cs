// ScrapeProgress.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Per-stage progress counters for a scrape job. Lives as an optional
///     sub-document on <see cref="JobRecord" /> because scrape is the only
///     job type with a multi-stage pipeline whose intermediate counts the
///     live monitor dashboard surfaces; folding these into the generic
///     <c>ItemsProcessed/ItemsTotal/ItemsLabel</c> triple would lose the
///     per-stage breakdown.
///     The non-scrape rows on the unified <c>jobs</c> collection leave
///     this property null; the unified repository serializes a null value
///     as a missing field, so the storage cost on non-scrape rows is zero.
/// </summary>
public sealed class ScrapeProgress
{
    /// <summary>Pages enqueued for fetching since job start.</summary>
    public int PagesQueued { get; set; }

    /// <summary>Pages fetched from the source (success or rejection).</summary>
    public int PagesFetched { get; set; }

    /// <summary>Pages handed to the classification stage.</summary>
    public int PagesClassified { get; set; }

    /// <summary>Chunks produced by the chunker.</summary>
    public int ChunksGenerated { get; set; }

    /// <summary>Chunks for which embeddings were generated.</summary>
    public int ChunksEmbedded { get; set; }

    /// <summary>Chunks persisted to MongoDB.</summary>
    public int ChunksCompleted { get; set; }

    /// <summary>Pages whose full chunk set finished embedding and indexing.</summary>
    public int PagesCompleted { get; set; }
}
