// ReembedResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Summary returned by reembed_library. Counts what was processed and
///     records the provider/model/dimensions the chunks were re-embedded
///     against so the caller can verify the swap landed as expected.
/// </summary>
public record ReembedResult
{
    /// <summary>
    ///     Library the reembed applied to.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version the reembed applied to.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     Number of chunks re-embedded (or that would have been, when
    ///     DryRun is true).
    /// </summary>
    public int Processed { get; init; }

    /// <summary>
    ///     Embedding provider that produced the new vectors. Mirrors
    ///     IEmbeddingProvider.ProviderId at run time and is persisted to
    ///     LibraryVersionRecord.EmbeddingProviderId on a non-dry run.
    /// </summary>
    public required string EmbeddingProviderId { get; init; }

    /// <summary>
    ///     Specific embedding model used. Mirrors
    ///     IEmbeddingProvider.ModelName at run time and is persisted to
    ///     LibraryVersionRecord.EmbeddingModelName on a non-dry run.
    /// </summary>
    public required string EmbeddingModelName { get; init; }

    /// <summary>
    ///     Dimensionality of the new vectors. Persisted to
    ///     LibraryVersionRecord.EmbeddingDimensions on a non-dry run.
    /// </summary>
    public int EmbeddingDimensions { get; init; }

    /// <summary>
    ///     The previous EmbeddingProviderId stored on the LibraryVersion,
    ///     before this reembed ran. Useful for the caller to confirm a
    ///     provider swap actually happened.
    /// </summary>
    public string? PreviousEmbeddingProviderId { get; init; }

    /// <summary>
    ///     The previous EmbeddingModelName stored on the LibraryVersion,
    ///     before this reembed ran.
    /// </summary>
    public string? PreviousEmbeddingModelName { get; init; }

    /// <summary>
    ///     The previous EmbeddingDimensions stored on the LibraryVersion,
    ///     before this reembed ran.
    /// </summary>
    public int? PreviousEmbeddingDimensions { get; init; }

    /// <summary>
    ///     True when this run made no writes (dry-run mode).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    ///     Returned when the (library, version) has no chunks to re-embed.
    ///     Caller should run scrape_docs first.
    /// </summary>
    public bool NothingToDo { get; init; }
}
