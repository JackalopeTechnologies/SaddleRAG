// SubjectSearchMetadata.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Enriched subject metadata keyed to one result chunk.</summary>
public sealed record SubjectSearchMetadata
{
    public required string ChunkId { get; init; }

    public required string TaxonomyVersion { get; init; }

    public required bool NeedsReview { get; init; }

    public IReadOnlyList<SubjectSearchPresentation> Subjects { get; init; } = [];
}
