// DocumentRevisionRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Immutable acquisition metadata for a source document in one library
///     version. Candidate records may be replaced by a retry; published
///     records are immutable.
/// </summary>
public record DocumentRevisionRecord
{
    public required string Id { get; init; }

    public required string DocumentId { get; init; }

    public required string LibraryId { get; init; }

    public required string Version { get; init; }

    public required string ScanRunId { get; init; }

    public required DocumentRevisionState State { get; init; }

    public DateTime? SourceModifiedAtUtc { get; init; }

    public required DateTime AcquiredAtUtc { get; init; }

    public required string OriginalArtifactHash { get; init; }

    public required long OriginalByteLength { get; init; }

    public required string OriginalMediaType { get; init; }

    /// <summary>URL discovered by the crawler before retries or redirects.</summary>
    public string? OriginalUrl { get; init; }

    /// <summary>Exact URL supplied to the successful HTTP navigation.</summary>
    public string? AttemptedUrl { get; init; }

    /// <summary>Final response URL after redirects.</summary>
    public string? FinalUrl { get; init; }

    /// <summary>Source ETag retained for later refresh decisions.</summary>
    public string? SourceETag { get; init; }

    /// <summary>Source Last-Modified value normalized to UTC.</summary>
    public DateTime? SourceLastModifiedAtUtc { get; init; }

    public string? ExtractionArtifactHash { get; init; }

    public long? ExtractionByteLength { get; init; }

    public string? ExtractionMediaType { get; init; }

    public DocumentExtractionProvenance? ExtractionProvenance { get; init; }

    public DateTime? PublishedAtUtc { get; init; }

    public string? FailureDetail { get; init; }

    /// <summary>
    ///     Exact managed-artifact ownership committed by the local repository.
    ///     Empty on legacy/imported revisions whose blobs must remain
    ///     deletion-ineligible.
    /// </summary>
    public IReadOnlyList<DocumentRevisionArtifactClaim> ArtifactClaims { get; init; } = [];
}
