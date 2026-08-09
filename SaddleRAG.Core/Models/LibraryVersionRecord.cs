// LibraryVersionRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Serialization;
using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Metadata for a specific version of a library scrape.
/// </summary>
public record LibraryVersionRecord
{
    /// <summary>
    ///     Receiver-local package import that owns this version's publication.
    ///     Excluded from bundle JSON so source scan provenance remains portable.
    /// </summary>
    [JsonIgnore]
    public string? ImportOperationId { get; init; }

    /// <summary>
    ///     Publication lifecycle. The default preserves legacy rows that do
    ///     not contain this field.
    /// </summary>
    public VersionPublicationState PublicationState { get; init; } = VersionPublicationState.Published;

    /// <summary>
    ///     Diagnostic detail retained when publication fails or is cancelled.
    /// </summary>
    public string? PublicationError { get; init; }

    /// <summary>
    ///     Manual directory scan that owns this version's publication lease.
    ///     Null for web ingestion and records created before directory leases.
    /// </summary>
    public string? ScanRunId { get; init; }

    /// <summary>
    ///     Directory registration revision captured when the scan claimed
    ///     this publication lease. Null for web ingestion and legacy rows.
    /// </summary>
    public long? RegistrationRevision { get; init; }

    /// <summary>
    ///     True after the owning directory scan has atomically qualified a
    ///     candidate for cleanup. Other scans remain blocked until cleanup
    ///     removes the version row or records its failure.
    /// </summary>
    public bool CleanupInProgress { get; init; }

    /// <summary>
    ///     Unique identifier. Example: "infragistics-wpf-25.2"
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Parent library identifier.
    /// </summary>
    public required string LibraryId { get; init; }

    /// <summary>
    ///     Version string for this scrape.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    ///     When this version was scraped.
    /// </summary>
    public required DateTime ScrapedAt { get; init; }

    /// <summary>
    ///     Total pages fetched.
    /// </summary>
    public required int PageCount { get; init; }

    /// <summary>
    ///     Total chunks generated.
    /// </summary>
    public required int ChunkCount { get; init; }

    /// <summary>
    ///     Embedding provider used for this version's chunks.
    ///     Queries must use the same provider for vector similarity.
    /// </summary>
    public required string EmbeddingProviderId { get; init; }

    /// <summary>
    ///     Specific model name used for embeddings.
    ///     Example: "nomic-embed-text" â€” used to ensure the same
    ///     model is loaded at query time.
    /// </summary>
    public required string EmbeddingModelName { get; init; }

    /// <summary>
    ///     Dimensionality of the stored embeddings.
    /// </summary>
    public required int EmbeddingDimensions { get; init; }

    /// <summary>
    ///     Classifier backend that categorized this version's pages
    ///     ("onnx" or "ollama"). Null on documents written before classifier
    ///     provenance was recorded.
    /// </summary>
    public string? ClassifierBackend { get; init; }

    /// <summary>
    ///     Classifier model id used for this version (e.g.
    ///     "phi-3-mini-4k-instruct-directml"). Null on older documents.
    /// </summary>
    public string? ClassifierModel { get; init; }

    /// <summary>
    ///     Subject taxonomy revision used by this library version. Null for
    ///     versions created before subject classification was introduced.
    /// </summary>
    public string? SubjectTaxonomyVersion { get; init; }

    /// <summary>
    ///     Previous version this was compared against, if any.
    /// </summary>
    public string? PreviousVersion { get; init; }

    /// <summary>
    ///     Percentage of chunks with boundary issues detected during extraction.
    ///     Range: 0.0 to 100.0. Default 0.
    /// </summary>
    public double BoundaryIssuePct { get; set; }

    /// <summary>
    ///     Whether this library version is flagged as suspect by the detector pipeline.
    /// </summary>
    public bool Suspect { get; set; }

    /// <summary>
    ///     Reasons why this library version is marked suspect.
    /// </summary>
    public IReadOnlyList<string> SuspectReasons { get; set; } = [];

    /// <summary>
    ///     When the suspect status was last evaluated.
    /// </summary>
    public DateTime? LastSuspectEvaluatedAt { get; set; }
}
