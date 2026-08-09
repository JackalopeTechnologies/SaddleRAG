// SubjectCatalogRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Serialization;
using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>Immutable revision of one library's subject catalog.</summary>
public sealed record SubjectCatalogRecord
{
    /// <summary>
    ///     Receiver-local package import that owns this catalog candidate.
    ///     Excluded from bundle JSON so source scan provenance remains portable.
    /// </summary>
    [JsonIgnore]
    public string? ImportOperationId { get; init; }

    /// <summary>
    ///     Candidate catalogs remain private to their creating scan until
    ///     publication. The default preserves legacy rows as published.
    /// </summary>
    public SubjectCatalogPublicationState PublicationState { get; init; } =
        SubjectCatalogPublicationState.Published;

    public required string Id { get; init; }

    public required string LibraryId { get; init; }

    public required int Revision { get; init; }

    public required string TaxonomyVersion { get; init; }

    /// <summary>
    ///     Scan that created this revision. Null on catalogs written before
    ///     scan ownership was recorded.
    /// </summary>
    public string? ScanRunId { get; init; }

    public string? PreviousTaxonomyVersion { get; init; }

    public IReadOnlyList<SubjectConcept> Concepts { get; init; } = [];

    public required SubjectClassifierProvenance Provenance { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}
